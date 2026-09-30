using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace OpenVersus.Server.Proxy;

/// <summary>Where the proxy sends what, changeable while it runs (ovs-ctl -s proxy settings set ...).</summary>
public sealed class ProxySettings : IValidatableObject
{
    [Description("The C# http service the ported routes go to.")]
    [Url]
    public string CSharpUrl { get; set; } = "http://127.0.0.1:8000";

    [Description("The TS server everything else goes to.")]
    [Url]
    public string TsUrl { get; set; } = "http://127.0.0.1:18000";

    [Description("The routes that go to C#, as METHOD /path, separated by commas: the game's method (a GET the game sends as PUT with x-hydra-http-method matches too) and the route template as the C# endpoint declares it. Empty: everything goes to the TS server.")]
    public string PortedRoutes { get; set; } = "POST /access, DELETE /access, POST /sessions/auth/token, GET /commerce/products, GET /commerce/purchases/me, GET /commerce/steam/mtx_user_info/me, GET /friends/me, GET /friends/me/invitations/incoming, GET /friends/me/invitations/outgoing, GET /social/me/blocked, GET /accounts/wb_network/bulk, GET /profiles/bulk, GET /layout/dokken-layout-type/personalized/account-cosmetics-variant/{id}, GET /layout/dokken-layout-type/personalized/battlepass-variant/{id}, GET /layout/dokken-layout-type/personalized/currency-variant/{id}, GET /layout/dokken-layout-type/personalized/fighter-road-layout/{id}, GET /layout/dokken-layout-type/personalized/fighter-variant/{id}, GET /layout/dokken-layout-type/personalized/main-variant/{id}, GET /layout/dokken-layout-type/personalized/prestige-variant/{id}, GET /layout/dokken-layout-type/personalized/rift-variant/{id}, GET /layout/dokken-layout-type/personalized/skin-variant/{id}, GET /file_storage, GET /file_storage/openversus-update-required-keyart, GET /file_storage/openversus-update-required-thumbnail, GET /file_storage/beginnermode-carousel-keyart, GET /file_storage/beginnermode-carousel-thumbnail, GET /file_storage/harley-rift-s5-keyart, GET /file_storage/harley-rift-s5-thumbnail, GET /file_storage/s5-bp-carousel-keyart, GET /file_storage/s5-bp-carousel-thumbnail, GET /file_storage/t-discord-qa-carousel-keyart, GET /file_storage/t-discord-qa-carousel-thumbnail, GET /file_storage/wonderwoman-arena-keyart, GET /file_storage/wonderwoman-arena-thumbnail, PUT /drives/multiversus/sync, GET /leaderboards/bulk/score-and-rank/{id}, GET /profiles/{id}/inventory, GET /leaderboards/{id}/show, GET /leaderboards/{id}/around/{account}, GET /leaderboards/{id}/around/me, GET /matches/all/{id}, PUT /matches/{id}, GET /profiles/search_queries/get-by-username/run, PUT /batch, GET /ssc/invoke/get_country_code, GET /ssc/invoke/get_hiss_calendar_events, GET /ssc/invoke/get_calendar_events, GET /ssc/invoke/get_milestone_reward_tracks, GET /ssc/invoke/perks_get_all_pages, GET /ssc/invoke/ranked_data, GET /ssc/invoke/get_equipped_cosmetics";

    // Refused when set, so the value shown is always the one in use.
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        string? problem = null;
        try
        {
            ProxyRoutes.Parse(PortedRoutes);
        }
        catch (FormatException e)
        {
            problem = e.Message;
        }

        if (problem is not null)
        {
            yield return new ValidationResult(problem, [nameof(PortedRoutes)]);
        }
    }
}

/// <summary>
/// The proxy's YARP configuration from <see cref="ProxySettings"/>: a route per ported route to the C# cluster, and a
/// catch-all to the TS cluster. Rebuilt whenever the settings change.
/// </summary>
public static class ProxyRoutes
{
    public const string CSharpCluster = "csharp";
    public const string TsCluster = "ts";
    public const string MethodOverrideHeader = "x-hydra-http-method";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(ProxySettings settings)
    {
        var routes = new List<RouteConfig>();
        foreach (var (method, path) in Parse(settings.PortedRoutes))
        {
            string id = $"{method} {path}";
            if (method == "PUT")
            {
                // A real PUT: no override header, or one that says PUT.
                routes.Add(Route($"{id} (plain)", "PUT", path, new RouteHeader { Name = MethodOverrideHeader, Mode = HeaderMatchMode.NotExists }));
                routes.Add(Route($"{id} (override PUT)", "PUT", path, Override("PUT")));
            }
            else
            {
                routes.Add(Route(id, method, path, null));
                // The Hydra SDK sends some GETs (and could send others) as PUT with the real method in a header.
                routes.Add(Route($"{id} (override)", "PUT", path, Override(method)));
            }
        }

        routes.Add(new RouteConfig { RouteId = "everything else", ClusterId = TsCluster, Order = int.MaxValue, Match = new RouteMatch { Path = "{**rest}" } });
        return (routes,
        [
            Cluster(CSharpCluster, settings.CSharpUrl),
            Cluster(TsCluster, settings.TsUrl),
        ]);
    }

    /// <summary>"POST /access, GET /profiles/{id}" as (method, path) pairs; anything else is refused.</summary>
    public static IReadOnlyList<(string Method, string Path)> Parse(string? routes) => RouteList.Parse(routes);

    private static RouteHeader Override(string method) =>
        new() { Name = MethodOverrideHeader, Values = [method], Mode = HeaderMatchMode.ExactHeader, IsCaseSensitive = false };

    private static RouteConfig Route(string id, string method, string path, RouteHeader? header) => new()
    {
        RouteId = id,
        ClusterId = CSharpCluster,
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
            log.LogInformation("Routes to C# ({CSharp}): {Routes}; everything else to {Ts}", current.CSharpUrl, current.PortedRoutes, current.TsUrl);
        }
        catch (FormatException e)
        {
            log.LogError("Proxy:PortedRoutes not applied, the previous routes stay: {Error}", e.Message);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
