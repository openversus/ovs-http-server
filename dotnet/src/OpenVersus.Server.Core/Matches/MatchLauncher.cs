using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// Starting a match: what the TS server's custom lobby does when its host presses start (start_custom_match,
// modules/customLobby/lobby.service.ts), step for step, so the TS websocket and rollback code that read it cannot tell
// the difference. The websocket then tells the game (GameServerReadyNotification, matchmaking-complete,
// OnGameplayConfigNotified, and PerksLockedNotification once every player's perks are locked), and the rollback server,
// asking /ovs_get_registry, gets the humans and triggers game-server-instance-ready.
//
// Redis, written  match:{match} (the TS RedisMatch: one ticket holding every player but the spectators, isPasswordMatch,
//                 so no ELO) EX 20 min; match:{match}:perks:{bot} (the launch's bot perks, "[]" when none) EX 20 min for
//                 each bot (bots never send perks_lock, and the TS all-perks-locked check waits for every ticket
//                 player); {match} (the notification, which /ovs_get_registry reads) EX 20 min
// Published       match:notifications (the notification: the websocket sends the match to its players), then
//                 matchmaking:complete ({containerMatchId, playerIds, matchmakingRequestId, resultId})
//
// The notification may carry gameplayConfigOverride (merged over the websocket's GameplayConfig) and
// playerConfigOverrides ({player: fields merged over that player's config}): the websocket builds a PvP config, and a
// mode it does not know (rifts) replaces what differs. It may also carry gameplayConfigTemplate (the template_id the
// config is sent under instead of OnGameplayConfigNotified: a rift retry's RiftRetryNotification) and gameplayConfigData
// (fields added beside the config: PriorMatchId). See docs/MIGRATION-BRIDGES.md (2).
//
// The rollback port, as the TS rollbackService.ts picks it:
//   fixed (Rollback:OnDemand off, ON_DEMAND_ROLLBACK=0): servers already running; a random port in
//     [UdpPortLow, UdpPortHigh).
//   on demand (ON_DEMAND_ROLLBACK=1, prod): one short-lived server per match. The port is INCR rollback:current_port,
//     back to OnDemandPortLow (and the key set to it) past OnDemandPortHigh; then the deploy webhook gets a POST of
//     {entrypoint (CHANGEMEDEFAULTPORT replaced by the port), image, ovs_server, port, servicename, created_at 0}
//     (deploy-rollback-defaults.json's fields, here settings), signed X-Deploy-Key = hex HMAC-SHA1 of the body with
//     WebhookHmacSecret. The match is announced without waiting for the answer, as there: a failed deploy is logged,
//     and the players find no server. One difference: the TS server uses an INCR below OnDemandPortLow as it is
//     (the first INCR of an empty Redis is port 1); here it wraps to OnDemandPortLow like one above the range.
// Redis, written  rollback:current_port (on demand)

/// <summary>Rollback server settings.</summary>
public sealed class RollbackSettings
{
    [Description("Lowest UDP port of the running rollback servers a match is given (ROLLBACK_UDP_PORT_LOW).")]
    [Range(1, 65535)]
    public int UdpPortLow { get; set; } = 57000;

    [Description("One past the highest such port (ROLLBACK_UDP_PORT_HIGH; the TS server's randomInt excludes it).")]
    [Range(2, 65536)]
    public int UdpPortHigh { get; set; } = 57019;

    [Description("A rollback server deployed for each match through the deploy webhook (ON_DEMAND_ROLLBACK=1), instead of the running ones on UdpPortLow..UdpPortHigh.")]
    public bool OnDemand { get; set; }

    [Description("Lowest port an on-demand rollback server is given (ON_DEMAND_ROLLBACK_PORT_LOW).")]
    [Range(1, 65535)]
    public int OnDemandPortLow { get; set; } = 60000;

    [Description("Highest port an on-demand rollback server is given, included (ON_DEMAND_ROLLBACK_PORT_HIGH).")]
    [Range(1, 65535)]
    public int OnDemandPortHigh { get; set; } = 64000;

    [Description("Host of the deploy webhook (WEBHOOK_HOST).")]
    public string WebhookHost { get; set; } = "localhost";

    [Description("Port of the deploy webhook (WEBHOOK_PORT).")]
    [Range(1, 65535)]
    public int WebhookPort { get; set; } = 9001;

    [Description("Path of the deploy webhook (WEBHOOK_DEPLOY_PATH).")]
    public string WebhookDeployPath { get; set; } = "/hooks/deploy-rollback-server";

    [Description("Signs each deploy request (X-Deploy-Key, HMAC-SHA1 of the body) (WEBHOOK_HMAC_SECRET). The TS default is CHANGEME.")]
    [Secret]
    public string WebhookHmacSecret { get; set; } = "CHANGEME";

    [Description("What a deployed rollback server runs, with CHANGEMEDEFAULTPORT replaced by its port (deploy-rollback-defaults.json entrypoint).")]
    public string DeployEntrypoint { get; set; } = "\"dotnet\", \"OVS.Rollback.Server.dll\", \"CHANGEMEDEFAULTPORT\"";

    [Description("Image of a deployed rollback server (deploy-rollback-defaults.json image).")]
    public string DeployImage { get; set; } = "ovs-rollback-server-csharp:latest";

    [Description("Service name of a deployed rollback server (deploy-rollback-defaults.json servicename).")]
    public string DeployServiceName { get; set; } = "ovs-rollback-server-csharp";

    [Description("The OpenVersus server a deployed rollback server reports to (OVS_SERVER).")]
    public string OvsServer { get; set; } = "http://localhost:8000";

    [Description("The key a rollback server's calls carry in their MatchUpdateKey header (MATCHUPDATEKEY; the rollback server's Server__MatchUpdateKey). Unset, or the TS placeholder MisconfiguredMatchUpdateKey: every such call is refused.")]
    [Secret]
    public string MatchUpdateKey { get; set; } = "";
}

/// <summary>
/// A player in a match, as the TS RedisTeamEntry. A spectator (a custom lobby's) is in the notification only, with
/// isSpectator in place of isBot: not in the ticket, not counted.
/// </summary>
public sealed record MatchPlayer(string PlayerId, int PlayerIndex, int TeamIndex, bool IsHost, string Ip, bool IsBot, bool IsSpectator = false);

/// <summary>A match to start: its players (humans at the lowest player indexes), map and mode, and what the websocket's
/// PvP gameplay config needs replaced. <paramref name="BotPerks"/> are what each bot's perks are locked as (none when
/// null); <paramref name="NotificationFields"/> are added to the notification after rollbackPort (a custom game's
/// settings).</summary>
public sealed record MatchLaunch(
    string MatchType,
    string Map,
    string Mode,
    IReadOnlyList<MatchPlayer> Players,
    JsonObject? GameplayConfigOverride = null,
    JsonObject? PlayerConfigOverrides = null,
    string? ConfigTemplate = null,
    JsonObject? ConfigData = null,
    JsonArray? BotPerks = null,
    JsonObject? NotificationFields = null);

/// <summary>The started match.</summary>
public sealed record LaunchedMatch(string MatchId, int RollbackPort);

public interface IMatchLauncher
{
    /// <summary>Starts <paramref name="launch"/>; null when it cannot (no Redis, no rollback port).</summary>
    Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct = default);

    /// <summary>A rollback port for a new match (see the header); null when there is none.</summary>
    Task<int?> RollbackPortAsync(IDatabase redis);

    /// <summary>Asks for a rollback server on <paramref name="port"/> when servers are deployed per match (on demand), without waiting.</summary>
    void DeployIfOnDemand(int port, string matchId);
}

internal sealed class MatchLauncher(IServiceProvider services, IOptionsMonitor<RollbackSettings> settings, IHttpClientFactory http,
    TimeProvider time, ILogger<MatchLauncher> log) : IMatchLauncher
{
    public const string NotificationChannel = "match:notifications";
    public const string MatchmakingCompleteChannel = "matchmaking:complete";
    public const string CurrentPortKey = "rollback:current_port";
    public const string DeployClient = "rollback-deploy";
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(20);

    public async Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("A {Mode} match was not started: no Redis", launch.Mode);
            return null;
        }

        var rollback = settings.CurrentValue;
        if (await RollbackPortAsync(redis) is not { } rollbackPort)
        {
            log.LogError("A {Mode} match was not started: no rollback port (on demand {OnDemand}; fixed range {Low}..{High} exclusive, on-demand range {DemandLow}..{DemandHigh})",
                launch.Mode, rollback.OnDemand, rollback.UdpPortLow, rollback.UdpPortHigh, rollback.OnDemandPortLow, rollback.OnDemandPortHigh);
            return null;
        }

        string matchId = ObjectId.GenerateNewId().ToString();
        string resultId = ObjectId.GenerateNewId().ToString();
        string matchmakingRequestId = ObjectId.GenerateNewId().ToString();
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var playing = launch.Players.Where(p => !p.IsSpectator).ToList();

        var ticket = new JsonObject
        {
            ["party_size"] = playing.Count,
            ["players"] = new JsonArray([.. playing.Select(p => (JsonNode)new JsonObject { ["id"] = p.PlayerId, ["skill"] = 0, ["region"] = "local" })]),
            ["created_at"] = now,
            ["partyId"] = matchId,
            ["matchmakingRequestId"] = matchmakingRequestId,
            ["isPasswordMatch"] = true,
        };
        var match = new JsonObject
        {
            ["matchId"] = matchId,
            ["resultId"] = resultId,
            ["tickets"] = new JsonArray(ticket),
            ["status"] = "pending",
            ["createdAt"] = now,
            ["matchType"] = launch.MatchType,
            ["totalPlayers"] = playing.Count,
            ["rollbackPort"] = rollbackPort,
            ["isPasswordMatch"] = true,
        };
        await redis.StringSetAsync($"match:{matchId}", Js.Stringify(match), s_ttl);

        foreach (var bot in launch.Players.Where(p => p.IsBot))
        {
            await redis.StringSetAsync($"match:{matchId}:perks:{bot.PlayerId}", Js.Stringify(launch.BotPerks ?? []), s_ttl);
        }

        var notification = new JsonObject
        {
            ["players"] = new JsonArray([.. launch.Players.Select(p =>
            {
                var entry = new JsonObject
                {
                    ["playerId"] = p.PlayerId,
                    ["partyId"] = matchId,
                    ["playerIndex"] = p.PlayerIndex,
                    ["teamIndex"] = p.TeamIndex,
                    ["isHost"] = p.IsHost,
                    ["ip"] = p.Ip,
                };
                entry[p.IsSpectator ? "isSpectator" : "isBot"] = p.IsSpectator || p.IsBot;
                return (JsonNode)entry;
            })]),
            ["matchId"] = matchId,
            ["matchKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["map"] = launch.Map,
            ["mode"] = launch.Mode,
            ["rollbackPort"] = rollbackPort,
        };
        foreach (var (key, value) in launch.NotificationFields ?? [])
        {
            notification[key] = value?.DeepClone();
        }

        if (launch.GameplayConfigOverride is not null)
        {
            notification["gameplayConfigOverride"] = launch.GameplayConfigOverride.DeepClone();
        }

        if (launch.PlayerConfigOverrides is not null)
        {
            notification["playerConfigOverrides"] = launch.PlayerConfigOverrides.DeepClone();
        }

        if (launch.ConfigTemplate is not null)
        {
            notification["gameplayConfigTemplate"] = launch.ConfigTemplate;
        }

        if (launch.ConfigData is not null)
        {
            notification["gameplayConfigData"] = launch.ConfigData.DeepClone();
        }

        DeployIfOnDemand(rollbackPort, matchId);

        string json = Js.Stringify(notification);
        await redis.StringSetAsync(matchId, json, s_ttl);
        await redis.PublishAsync(RedisChannel.Literal(NotificationChannel), json);
        await redis.PublishAsync(RedisChannel.Literal(MatchmakingCompleteChannel), Js.Stringify(new JsonObject
        {
            ["containerMatchId"] = matchId,
            ["playerIds"] = new JsonArray([.. launch.Players.Select(p => (JsonNode)p.PlayerId)]),
            ["matchmakingRequestId"] = matchmakingRequestId,
            ["resultId"] = ObjectId.GenerateNewId().ToString(),
        }));

        log.LogInformation("Started {Mode} match {Match} on rollback port {Port}: {Players}", launch.Mode, matchId, rollbackPort,
            string.Join(", ", launch.Players.Select(p => $"{p.PlayerId} (team {p.TeamIndex}, index {p.PlayerIndex}{(p.IsBot ? ", bot" : "")})")));
        return new LaunchedMatch(matchId, rollbackPort);
    }

    public Task<int?> RollbackPortAsync(IDatabase redis)
    {
        var rollback = settings.CurrentValue;
        return rollback.OnDemand ? NextOnDemandPortAsync(redis, rollback) : Task.FromResult(RollbackPort(rollback));
    }

    public void DeployIfOnDemand(int port, string matchId)
    {
        var rollback = settings.CurrentValue;
        if (rollback.OnDemand)
        {
            _ = DeployAsync(DeployRequest(rollback, port), matchId);
        }
    }

    /// <summary>The next on-demand port: INCR rollback:current_port, wrapped to OnDemandPortLow outside the range.</summary>
    private async Task<int?> NextOnDemandPortAsync(IDatabase redis, RollbackSettings rollback)
    {
        if (rollback.OnDemandPortHigh < rollback.OnDemandPortLow)
        {
            return null;
        }

        long next = await redis.StringIncrementAsync(CurrentPortKey);
        int port = WrapOnDemandPort(next, rollback);
        if (port != next)
        {
            await redis.StringSetAsync(CurrentPortKey, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return port;
    }

    internal static int WrapOnDemandPort(long next, RollbackSettings rollback) =>
        next < rollback.OnDemandPortLow || next > rollback.OnDemandPortHigh ? rollback.OnDemandPortLow : (int)next;

    /// <summary>The deploy webhook request for a rollback server on <paramref name="port"/>: its URL, body and X-Deploy-Key.</summary>
    internal static (Uri Url, string Body, string Signature) DeployRequest(RollbackSettings rollback, int port)
    {
        string body = Js.Stringify(new JsonObject
        {
            ["entrypoint"] = rollback.DeployEntrypoint.Replace("CHANGEMEDEFAULTPORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["image"] = rollback.DeployImage,
            ["ovs_server"] = rollback.OvsServer,
            ["port"] = port,
            ["servicename"] = rollback.DeployServiceName,
            ["created_at"] = 0,
        });
        string signature = Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(rollback.WebhookHmacSecret), Encoding.UTF8.GetBytes(body)));
        return (new UriBuilder("http", rollback.WebhookHost, rollback.WebhookPort, rollback.WebhookDeployPath).Uri, body, signature);
    }

    // Sent without waiting, as the TS DeployInfo.Deploy: the answer and any failure are only logged.
    private async Task DeployAsync((Uri Url, string Body, string Signature) request, string matchId)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
            {
                Content = new StringContent(request.Body, Encoding.UTF8, "application/json"),
            };
            message.Headers.Add("X-Deploy-Key", request.Signature);
            using var response = await http.CreateClient(DeployClient).SendAsync(message);
            log.LogInformation("Deploy webhook for match {Match}: {Status} {Answer} ({Request})", matchId, (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(), request.Body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError("Deploy webhook for match {Match} failed: {Error} ({Request})", matchId, e.Message, request.Body);
        }
    }

    /// <summary>A port in [UdpPortLow, UdpPortHigh), as the TS randomInt; null when on-demand or the range is empty.</summary>
    internal static int? RollbackPort(RollbackSettings settings) =>
        !settings.OnDemand && settings.UdpPortHigh > settings.UdpPortLow ? Random.Shared.Next(settings.UdpPortLow, settings.UdpPortHigh) : null;
}

public static class MatchLauncherHosting
{
    public static WebApplicationBuilder AddMatchLauncher(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RollbackSettings>("Rollback");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMatchLauncher, MatchLauncher>();
        builder.Services.AddHttpClient(MatchLauncher.DeployClient, c => c.Timeout = TimeSpan.FromSeconds(30));
        return builder;
    }
}
