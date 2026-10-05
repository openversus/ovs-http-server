using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace OpenVersus.Server.Proxy;

/// <summary>Where the proxy sends what, changeable while it runs (ovsctl -s proxy settings set ...).</summary>
public sealed class ProxySettings : IValidatableObject
{
    [Description("The C# http service the ported routes go to, unless another service owns them (Proxy:Services).")]
    [Url]
    public string CSharpUrl { get; set; } = "http://127.0.0.1:8000";

    [Description("The TS server everything else goes to.")]
    [Url]
    public string TsUrl { get; set; } = "http://127.0.0.1:18000";

    [Description("The C# services other than http, as name=url separated by commas (access=http://127.0.0.1:18201). A ported route goes to the service that owns it (docs/routes.json, owner) when it is listed here, else to Proxy:CSharpUrl.")]
    public string Services { get; set; } = "";

    [Description("The routes that go to C#, as METHOD /path, separated by commas: the game's method (a GET the game sends as PUT with x-hydra-http-method matches too) and the route template as the C# endpoint declares it. Empty: everything goes to the TS server.")]
    public string PortedRoutes { get; set; } = "POST /access, DELETE /access, POST /sessions/auth/token, GET /commerce/products, GET /commerce/purchases/me, GET /commerce/steam/mtx_user_info/me, GET /friends/me, GET /friends/me/invitations/incoming, GET /friends/me/invitations/outgoing, GET /social/me/blocked, GET /accounts/wb_network/bulk, GET /profiles/bulk, GET /layout/dokken-layout-type/personalized/account-cosmetics-variant/{id}, GET /layout/dokken-layout-type/personalized/battlepass-variant/{id}, GET /layout/dokken-layout-type/personalized/currency-variant/{id}, GET /layout/dokken-layout-type/personalized/fighter-road-layout/{id}, GET /layout/dokken-layout-type/personalized/fighter-variant/{id}, GET /layout/dokken-layout-type/personalized/main-variant/{id}, GET /layout/dokken-layout-type/personalized/prestige-variant/{id}, GET /layout/dokken-layout-type/personalized/rift-variant/{id}, GET /layout/dokken-layout-type/personalized/skin-variant/{id}, GET /file_storage, GET /file_storage/openversus-update-required-keyart, GET /file_storage/openversus-update-required-thumbnail, GET /file_storage/beginnermode-carousel-keyart, GET /file_storage/beginnermode-carousel-thumbnail, GET /file_storage/harley-rift-s5-keyart, GET /file_storage/harley-rift-s5-thumbnail, GET /file_storage/s5-bp-carousel-keyart, GET /file_storage/s5-bp-carousel-thumbnail, GET /file_storage/t-discord-qa-carousel-keyart, GET /file_storage/t-discord-qa-carousel-thumbnail, GET /file_storage/wonderwoman-arena-keyart, GET /file_storage/wonderwoman-arena-thumbnail, PUT /drives/multiversus/sync, GET /leaderboards/bulk/score-and-rank/{id}, GET /profiles/{id}/inventory, GET /leaderboards/{id}/show, GET /leaderboards/{id}/around/{account}, GET /leaderboards/{id}/around/me, GET /matches/all/{id}, PUT /matches/{id}, GET /profiles/search_queries/get-by-username/run, PUT /batch, GET /ssc/invoke/get_country_code, GET /ssc/invoke/get_hiss_calendar_events, GET /ssc/invoke/get_calendar_events, GET /ssc/invoke/get_milestone_reward_tracks, GET /ssc/invoke/perks_get_all_pages, GET /ssc/invoke/ranked_data, GET /ssc/invoke/get_equipped_cosmetics, GET /objects/preferences/unique/{id}/{key}, GET /global_configuration_types/calendarflags/global_configurations, GET /global_configuration_types/wwshopconfiguration/global_configurations, GET /ssc/invoke/hiss_amalgamation, PUT /ssc/invoke/hiss_amalgamation, GET /ssc/invoke/load_rifts, GET /ssc/invoke/get_or_create_rift_state, PUT /ssc/invoke/create_rift_lobby, PUT /ssc/invoke/lock_rift_lobby_loadout, PUT /ssc/invoke/start_rift_node, PUT /ssc/invoke/retry_current_rift_node, POST /ssc/invoke/attempt_daily_refresh, PUT /ssc/invoke/equip_taunt, PUT /ssc/invoke/equip_stat_tracker, PUT /ssc/invoke/equip_announcer_pack, PUT /ssc/invoke/equip_banner, PUT /ssc/invoke/equip_ringout_vfx, PUT /ssc/invoke/set_profile_icon, PUT /ssc/invoke/ranked_claim_end_of_season_rewards, PUT /ssc/invoke/game_install, PUT /ssc/invoke/game_launch_event, PUT /ssc/invoke/cancel_party_invite, PUT /ssc/invoke/decline_party_invite, PUT /ssc/invoke/update_party_game_modes, POST /ssc/invoke/claim_mission_rewards, PUT /ssc/invoke/perks_set_character_page, PUT /ssc/invoke/perks_absent, PUT /ssc/invoke/update_player_preferences, POST /ssc/invoke/get_or_create_mission_object, PUT /ssc/invoke/claim_all_milestone_reward_track_tiers, PUT /ssc/invoke/create_party_lobby, PUT /ssc/invoke/create_party, PUT /ssc/invoke/set_mode_for_lobby, PUT /ssc/invoke/invite_to_player_lobby, PUT /ssc/invoke/join_party_lobby, PUT /ssc/invoke/leave_player_lobby, PUT /ssc/invoke/set_lobby_joinable, PUT /ssc/invoke/set_lobby_not_joinable, PUT /ssc/invoke/autoparty_join, PUT /ssc/invoke/set_ready_for_lobby, PUT /ssc/invoke/lock_lobby_loadout, PUT /ssc/invoke/create_custom_game_lobby, PUT /ssc/invoke/join_custom_game_lobby, PUT /ssc/invoke/update_team_style_for_custom_game, PUT /ssc/invoke/update_int_setting_for_custom_game, PUT /ssc/invoke/set_game_mode_for_custom_game, PUT /ssc/invoke/set_enabled_maps_for_custom_game, PUT /ssc/invoke/set_player_handicap_for_custom_game, PUT /ssc/invoke/switch_custom_game_lobby_team, PUT /ssc/invoke/add_custom_game_bot, PUT /ssc/invoke/update_custom_game_bot_fighter, PUT /ssc/invoke/reset_custom_lobby_to_defaults, PUT /ssc/invoke/promote_to_lobby_leader, PUT /ssc/invoke/kick_from_lobby, PUT /ssc/invoke/set_world_buffs_for_custom_game, PUT /ssc/invoke/lobby_code, PUT /ssc/invoke/start_custom_match, GET /matches/{id}, POST /matches/matchmaking/1v1-retail/request, POST /matches/matchmaking/ranked-1v1-retail/request, POST /matches/matchmaking/2v2-retail/request, POST /matches/matchmaking/casual-retail/request, PUT /ssc/invoke/casual_queue, POST /matches/matchmaking/request/{id}/cancel, PUT /ssc/invoke/perks_lock, PUT /ssc/invoke/toast_player, PUT /ssc/invoke/match_set_checkin, PUT /ssc/invoke/match_set_absent, PUT /ssc/invoke/match_set_concede, PUT /ssc/invoke/faceoff_timeout, PUT /ssc/invoke/submit_end_of_match_stats, GET /ssc/invoke/check_training_server_ready, PUT /ssc/invoke/check_training_server_ready, POST /ssc/invoke/check_training_server_ready, DELETE /ssc/invoke/check_training_server_ready, GET /ssc/invoke/get_or_create_my_match_config, PUT /ssc/invoke/get_or_create_my_match_config, POST /ssc/invoke/get_or_create_my_match_config, DELETE /ssc/invoke/get_or_create_my_match_config, GET /ssc/invoke/sync_match_config, PUT /ssc/invoke/sync_match_config, POST /ssc/invoke/sync_match_config, DELETE /ssc/invoke/sync_match_config, POST /ovs_match_inputs";

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

        string? services = null;
        try
        {
            ProxyRoutes.ParseServices(Services);
        }
        catch (FormatException e)
        {
            services = e.Message;
        }

        if (services is not null)
        {
            yield return new ValidationResult(services, [nameof(Services)]);
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
        var services = ParseServices(settings.Services);
        var routes = new List<RouteConfig>();
        foreach (var (method, path) in Parse(settings.PortedRoutes))
        {
            string id = $"{method} {path}";
            // The owner's service when it runs apart (a route template matches itself: {id} is a parameter).
            string owner = RouteOwners.Routes.OwnerOf(method, new Microsoft.AspNetCore.Http.PathString(path));
            string cluster = services.ContainsKey(owner) ? ServiceCluster(owner) : CSharpCluster;
            if (method == "PUT")
            {
                // A real PUT: no override header, or one that says PUT.
                routes.Add(Route($"{id} (plain)", cluster, "PUT", path, new RouteHeader { Name = MethodOverrideHeader, Mode = HeaderMatchMode.NotExists }));
                routes.Add(Route($"{id} (override PUT)", cluster, "PUT", path, Override("PUT")));
            }
            else
            {
                routes.Add(Route(id, cluster, method, path, null));
                // The Hydra SDK sends some GETs (and could send others) as PUT with the real method in a header.
                routes.Add(Route($"{id} (override)", cluster, "PUT", path, Override(method)));
            }
        }

        routes.Add(new RouteConfig { RouteId = "everything else", ClusterId = TsCluster, Order = int.MaxValue, Match = new RouteMatch { Path = "{**rest}" } });
        return (routes,
        [
            Cluster(CSharpCluster, settings.CSharpUrl),
            Cluster(TsCluster, settings.TsUrl),
            .. services.Select(s => Cluster(ServiceCluster(s.Key), s.Value)),
        ]);
    }

    public static string ServiceCluster(string service) => $"service:{service}";

    /// <summary>"access=http://host:port, web=..." as service name to URL; an unknown service or a bad URL is refused.</summary>
    public static IReadOnlyDictionary<string, string> ParseServices(string? services)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in (services ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || KnownServices.Find(parts[0]) is null || !Uri.TryCreate(parts[1], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                throw new FormatException($"\"{entry}\" is not service=url with a known service ({string.Join(", ", KnownServices.All.Select(s => s.Name))})");
            }

            parsed[parts[0]] = parts[1];
        }

        return parsed;
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
