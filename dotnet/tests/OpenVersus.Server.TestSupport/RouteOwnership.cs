using System.Net;
using Microsoft.AspNetCore.Http;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.TestSupport;

/// <summary>
/// Whether a service answers exactly the routes it owns (docs/routes.json, owner): each of its own reaches an endpoint
/// of its own (a stub with the stub status, or a ported endpoint, which names itself either way); each of another's
/// reaches nothing here (404, no endpoint) or, in the service the router sends unknown paths to, the fallback.
/// </summary>
public static class RouteOwnership
{
    /// <param name="stillHere">Owners whose routes have not moved out of this service yet (the HTTP service's).</param>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(HttpClient client, string service, bool fallback = false, int stubStatus = 501,
        IReadOnlyCollection<string>? stillHere = null)
    {
        var problems = new List<string>();
        foreach (var route in RouteTable.Load())
        {
            string path = RouteTable.Concrete(route.Path);
            // Where templates overlap, the concrete path may belong to a more literal route of another service's.
            string owner = RouteOwners.Routes.OwnerOf(route.Method is "?" ? "GET" : route.Method, new PathString(path));
            bool mine = owner == service || stillHere?.Contains(owner) == true;
            if (mine)
            {
                // A route whose method is not known owns whichever verbs another route of its path leaves: one is enough.
                string? missed = null;
                foreach (string verb in route.Method is "?" ? RouteTable.AllVerbs : [route.Method])
                {
                    var (status, endpoint, stub) = await SendAsync(client, verb, path);
                    if (endpoint != "" && (stub == "" || status == stubStatus))
                    {
                        missed = null;
                        break;
                    }

                    missed ??= $"{verb} {route.Path} ({route.Owner}): status {status}, endpoint '{endpoint}', stub '{stub}'";
                }

                if (missed is not null)
                {
                    problems.Add(missed);
                }
            }
            else
            {
                string verb = route.Method is "?" ? "GET" : route.Method;
                var (status, endpoint, stub) = await SendAsync(client, verb, path);
                if (endpoint != "" || (fallback ? stub != Stub.FallbackName : status != (int)HttpStatusCode.NotFound))
                {
                    problems.Add($"{verb} {route.Path} is {route.Owner}'s, but {service} answers it: status {status}, endpoint '{endpoint}', stub '{stub}'");
                }
            }
        }

        return problems;
    }

    private static async Task<(int Status, string Endpoint, string Stub)> SendAsync(HttpClient client, string verb, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), path);
        using var response = await client.SendAsync(request);
        return ((int)response.StatusCode,
            response.Headers.TryGetValues(Stub.EndpointHeader, out var e) ? e.Single() : "",
            response.Headers.TryGetValues(Stub.Header, out var s) ? s.Single() : "");
    }
}
