using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace OpenVersus.Server.Proxy;

/// <summary>Where the proxy sends what, changeable while it runs (ovsctl -s proxy settings set ...).</summary>
public sealed class ProxySettings : IValidatableObject
{
    [Description("The C# side everything goes to unless the route is listed in Proxy:TsRoutes: the router, which sends each route to the service that owns it (or the http service alone).")]
    [Url]
    public string CSharpUrl { get; set; } = "http://127.0.0.1:8000";

    [Description("The TS server the routes in Proxy:TsRoutes go to.")]
    [Url]
    public string TsUrl { get; set; } = "http://127.0.0.1:18000";

    [Description("The routes the TS server still answers, as METHOD /path, separated by commas: the game's method (a GET the game sends as PUT with x-hydra-http-method matches too) and the path as the TS server declares it; a route constraint keeps a ported literal path out of a parameter ({id:regex(^(?!bulk$).+$)}). Everything else goes to C#. Empty: everything goes to C#.")]
    public string TsRoutes { get; set; } = "PUT /accounts/me/relationships/{id}/block, PUT /accounts/me/relationships/{id}/unblock, PUT /friends/me/invitations/{id}/accept, PUT /friends/me/invitations/{id}/decline, PUT /friends/me/unfriend/{id}, PUT /social/me/block/{id}, PUT /social/me/unblock/{id}, DELETE /ovs/friends/{friendId}, POST /ovs/friends/accept, POST /ovs/friends/block, POST /ovs/friends/decline, GET /admin/banner, GET /api/admin/banner/online-count, POST /api/admin/banner, POST /syncAsset, GET /api/leaderboard/{mode}, GET /api/leaderboard/{mode}/me, GET /api/matches, GET /home, GET /leaderboard, GET /matches, GET /stats";

    // Refused when set, so the value shown is always the one in use.
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        string? problem = null;
        try
        {
            ProxyRoutes.Parse(TsRoutes);
        }
        catch (FormatException e)
        {
            problem = e.Message;
        }

        if (problem is not null)
        {
            yield return new ValidationResult(problem, [nameof(TsRoutes)]);
        }
    }
}

/// <summary>
/// The proxy's YARP configuration from <see cref="ProxySettings"/>: a route per route the TS server still answers, to the
/// TS cluster, and a catch-all to the C# cluster. Rebuilt whenever the settings change.
/// </summary>
public static class ProxyRoutes
{
    public const string CSharpCluster = "csharp";
    public const string TsCluster = "ts";
    public const string MethodOverrideHeader = "x-hydra-http-method";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(ProxySettings settings)
    {
        var routes = new List<RouteConfig>();
        foreach (var (method, path) in Parse(settings.TsRoutes))
        {
            string id = $"{method} {path}";
            if (method == "PUT")
            {
                // A real PUT: no override header, or one that says PUT.
                routes.Add(Route($"{id} (plain)", TsCluster, "PUT", path, new RouteHeader { Name = MethodOverrideHeader, Mode = HeaderMatchMode.NotExists }));
                routes.Add(Route($"{id} (override PUT)", TsCluster, "PUT", path, Override("PUT")));
            }
            else
            {
                routes.Add(Route(id, TsCluster, method, path, null));
                // The Hydra SDK sends some GETs (and could send others) as PUT with the real method in a header.
                routes.Add(Route($"{id} (override)", TsCluster, "PUT", path, Override(method)));
            }
        }

        routes.Add(new RouteConfig { RouteId = "everything else", ClusterId = CSharpCluster, Order = int.MaxValue, Match = new RouteMatch { Path = "{**rest}" } });
        return (routes, [Cluster(CSharpCluster, settings.CSharpUrl), Cluster(TsCluster, settings.TsUrl)]);
    }

    /// <summary>"POST /access, GET /profiles/{id}" as (method, path) pairs; anything else is refused.</summary>
    public static IReadOnlyList<(string Method, string Path)> Parse(string? routes) => RouteList.Parse(routes);

    private static RouteHeader Override(string method) =>
        new() { Name = MethodOverrideHeader, Values = [method], Mode = HeaderMatchMode.ExactHeader, IsCaseSensitive = false };

    private static RouteConfig Route(string id, string cluster, string method, string path, RouteHeader? header) => new()
    {
        RouteId = id,
        ClusterId = cluster,
        Match = new RouteMatch { Methods = [method], Path = path, Headers = header is null ? null : [header] },
    };

    private static ClusterConfig Cluster(string id, string url) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig> { [id] = new() { Address = url } },
    };
}

/// <summary>Keeps YARP's routes in step with <see cref="ProxySettings"/>.</summary>
internal sealed class ProxyRoutesSync(IOptionsMonitor<ProxySettings> settings, InMemoryConfigProvider config, ILogger<ProxyRoutesSync> log) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = settings.OnChange(Apply);
        return Task.CompletedTask;
    }

    private void Apply(ProxySettings current)
    {
        try
        {
            var (routes, clusters) = ProxyRoutes.Build(current);
            config.Update(routes, clusters);
            log.LogInformation("Routes still answered by the TS server ({Ts}): {Routes}; everything else to C# ({CSharp})", current.TsUrl, current.TsRoutes, current.CSharpUrl);
        }
        catch (FormatException e)
        {
            log.LogError("Proxy:TsRoutes not applied, the previous routes stay: {Error}", e.Message);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
