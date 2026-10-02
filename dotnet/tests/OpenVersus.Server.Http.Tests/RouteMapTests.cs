using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// Every route in docs/routes.json reaches an endpoint of its own (named by the X-OVS-Endpoint header, or X-OVS-Stub for
/// the fallback): not the fallback, not the SSC catch-all, and not
/// an endpoint another route also lands on (which would mean one of the two is shadowed). Plus the routing rules the
/// game depends on: the Hydra method override, the SSC catch-all and the fallback.
/// </summary>
public sealed class RouteMapTests : IClassFixture<GameAppFactory>
{
    private static readonly string[] s_allVerbs = ["GET", "PUT", "POST", "DELETE"];
    private static readonly int s_stubStatus = new StubSettings().StatusCode;
    private readonly HttpClient _client;

    public RouteMapTests(GameAppFactory factory)
    {
        _client = factory.CreateGameClient();
    }

    public sealed record Route(string Method, string Path, string Kind, string Area, string Owner);

    /// <summary>The services that answer routes (owners.tsv, copied into routes.json by gen_routes.py).</summary>
    internal static readonly string[] Owners = ["http", "access", "social", "lobbies", "matchflow", "web"];

    /// <summary>Owners whose routes still live in this service: each moves out to its own executable (web, access and social have).</summary>
    internal static readonly string[] StillHere = ["http", "matchflow"];

    internal static IReadOnlyList<Route> LoadRoutes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "routes.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "docs", "routes.json")));
        return doc.RootElement.EnumerateArray()
            .Select(r => new Route(r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!, r.GetProperty("kind").GetString()!, r.GetProperty("area").GetString()!,
                r.TryGetProperty("owner", out var owner) ? owner.GetString() ?? "" : ""))
            .Where(r => !r.Path.StartsWith("/.*", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>A concrete path for a template: each parameter becomes a value, a trailing * one more segment.</summary>
    private static string Concrete(string template) =>
        Regex.Replace(template.EndsWith("/*", StringComparison.Ordinal) ? template[..^1] + "x9/y9" : template, @"\{[^}]+\}", "x1");

    private async Task<(HttpStatusCode Status, string Stub, string Endpoint)> SendAsync(string method, string path, string? overrideMethod = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (overrideMethod is not null)
        {
            request.Headers.Add(HydraMethodOverride.Header, overrideMethod);
        }

        using var response = await _client.SendAsync(request);
        return (response.StatusCode,
            response.Headers.TryGetValues(Stub.Header, out var stub) ? stub.Single() : "",
            response.Headers.TryGetValues(Stub.EndpointHeader, out var endpoint) ? endpoint.Single() : "");
    }

    [Fact]
    public void EveryRouteHasAnOwner()
    {
        var unowned = LoadRoutes().Where(r => !Owners.Contains(r.Owner)).Select(r => $"{r.Method} {r.Path}: '{r.Owner}'").ToList();
        Assert.Empty(unowned);
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwnsAndSendsTheRestToTheFallback()
    {
        var problems = await RouteOwnership.ProblemsAsync(_client, KnownServices.Http.Name, fallback: true, stillHere: StillHere, catchAlls: ["SscUnlisted"]);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task EveryRouteReachesAnEndpointOfItsOwn()
    {
        var owners = new Dictionary<string, string>();
        var problems = new List<string>();
        var routes = LoadRoutes().Where(r => StillHere.Contains(r.Owner)).ToList();
        // Not a count to keep in step with the moves: only a guard against reading an empty or wrong file.
        Assert.True(routes.Count > 100, $"routes.json has only {routes.Count} routes here");
        foreach (var route in routes)
        {
            // A route with an unknown method may share its path with routes whose methods are known; it only owns
            // the verbs they leave, so it is checked with the ones that reach a stub that is not someone else's.
            var verbs = route.Method is "?" or "ALL" ? s_allVerbs : [route.Method];
            var reached = new HashSet<string>();
            foreach (var verb in verbs)
            {
                // A ported endpoint answers whatever it answers (here, with no Mongo or Redis, that it cannot); a stub
                // answers the stub status. Either way the endpoint names itself.
                var (status, stub, endpoint) = await SendAsync(verb, Concrete(route.Path));
                string name = endpoint != "" ? endpoint : stub;
                if (name == "" || (stub != "" && (int)status != s_stubStatus))
                {
                    problems.Add($"{verb} {route.Path}: status {(int)status}, stub '{stub}', endpoint '{endpoint}'");
                    continue;
                }

                reached.Add(name);
            }

            if (reached.Contains(Stub.FallbackName))
            {
                problems.Add($"{route.Method} {route.Path}: reached the fallback");
            }

            if (route.Area == "ssc" && reached.Contains("SscUnlisted"))
            {
                problems.Add($"{route.Method} {route.Path}: reached the SSC catch-all, not its own endpoint");
            }

            if (route.Method is not ("?" or "ALL"))
            {
                var stub = reached.SingleOrDefault();
                if (stub is not null && owners.TryGetValue(stub, out var other))
                {
                    problems.Add($"{route.Method} {route.Path} and {other} both reach {stub}");
                }
                else if (stub is not null)
                {
                    owners[stub] = $"{route.Method} {route.Path}";
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task TheHydraOverrideHeaderPicksTheRealMethod()
    {
        // The game sends both of these as PUT to the same path; only the header differs.
        Assert.Equal("GetProfilesByIdInventory", (await SendAsync("PUT", "/profiles/abc/inventory", overrideMethod: "GET")).Endpoint);
        Assert.Equal("PutProfilesByIdInventory", (await SendAsync("PUT", "/profiles/abc/inventory")).Stub);
        Assert.Equal("GetProfilesBulk", (await SendAsync("PUT", "/profiles/bulk", overrideMethod: "GET")).Endpoint);
    }

    [Fact]
    public async Task AnOverrideThatIsNotAMethodIsIgnored()
    {
        Assert.Equal("PutProfilesByIdInventory", (await SendAsync("PUT", "/profiles/abc/inventory", overrideMethod: "BOGUS")).Stub);
    }

    [Fact]
    public async Task AnUnlistedSscNameReachesTheCatchAll()
    {
        var (status, stub, _) = await SendAsync("PUT", "/ssc/invoke/some_name_the_map_does_not_have");
        Assert.Equal(s_stubStatus, (int)status);
        Assert.Equal("SscUnlisted", stub);
    }

    [Fact]
    public async Task AnUnknownPathReachesTheFallback()
    {
        var (status, stub, _) = await SendAsync("GET", "/definitely/not/a/route");
        Assert.Equal(s_stubStatus, (int)status);
        Assert.Equal(Stub.FallbackName, stub);
    }
}
