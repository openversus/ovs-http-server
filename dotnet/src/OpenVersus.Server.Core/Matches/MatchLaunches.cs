using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A match is announced once it is made: by MatchLauncher (custom lobbies, Casual bots and rematches, rift nodes), by the
// matchmaker (MatchmakingWorker) and by a ranked set's next game (RankedSets), with the matchmaking-complete each party is
// owed (MatchComplete: its players and the request it answers; none for a set's next game). The notification is kept at
// {match} (EX 20 min: /ovs_register and everything after it read it), then:
//   Realtime:Gateway off: published on match:notifications, as the TS server did, and matchmaking-complete sent at once;
//     the TS websocket tells the players (GameServerReadyNotification, then the config) and the match flow builds its own
//     copy of the config beside it (GameplayConfigBridge, GameplayConfigs:Mode; docs/MIGRATION-BRIDGES.md 9).
//   on: appended to the stream match:launched (with the matchmaking-completes), which the match flow reads as one consumer
//     group ("matchflow", MatchLaunchStream), so each match is told once whatever the number of replicas, and one launched
//     while no replica runs waits for the next. Per match, the config is built and kept first (GameplayConfigs, Mode On:
//     its writes beside it too), then, as the TS websocket's handleMatchFound, handleMatchMakingComplete and
//     handleSendGamePlayConfig sent them:
//       each player's game (bots have none; spectators included) sent GameServerReadyNotification {MatchKey, MatchID,
//       Port, template_id, IPAddress}: a P2P match, the player's own node (127.0.0.1, the port its client reported,
//       connections:{player} nodePort, else Rollback:P2PNodePort); else 127.0.0.1 for a player on the server's own
//       machine (the socket's address, realtime:conn:{player} ip, is loopback) and Rollback:UdpServerIp for everyone
//       else, with the match's rollback port (Rollback:UdpPort when it has none);
//       each party's matchmaking-complete;
//       the config, to the same players.
//     A match whose config cannot be built (the build fails, or leaves no config: no map, no players) is never played:
//     nobody is told it exists (no GameServerReadyNotification, no matchmaking-complete), it is abandoned ({match} and
//     match:{match} deleted, so its rollback server or node is answered "" and ends; a set it was a game of ends, its
//     pointers with it, as when a player queues again), and each player is told why by the OpenVersus client (an
//     admin_banner; its match_cancel would show a fixed "Opponent left the match"). A game still searching (its party's
//     ticket was its own request: the matchmaker's) is sent matchmaking-cancel for that request, which takes it back to
//     the lobby. Any other game (a custom lobby's, Casual bots', a rematch's, a rift node's, a set's next game, a
//     spectator) went to its loading screen ("Preparing for Battle...") on start, and nothing the server sends takes it
//     out of there (bench, 2026-10-06: matchmaking-cancel, the empty config and the client's match_cancel, which finds no
//     pre-match state then): its connection is closed after the banner (DelayedMessages, 5 s: the client polls every
//     2 s, then 3 s to read it), and the game goes back to its title screen to log in again.
//     Told at most once (match_announced:{match} SET NX EX 20 min, before the first send): a launch read again (a replica
//     that died mid-way, a retry) sends nothing twice. A launch whose {match} is gone (its 20 minutes are over: no
//     rollback server can register it any more) is acknowledged without a word. One that fails (Redis) stays pending;
//     any replica takes over launches left pending 10 s (XAUTOCLAIM), and drops one after 5 deliveries.
//
// Redis, written  {match} (the notification) EX 20 min; match:launched (XADD, MAXLEN ~10,000; fields match,
//                 notification, complete) or match:notifications (published); match_announced:{match} EX 20 min;
//                 abandoned: {match}, match:{match}, the set's ranked_set, ranked_set_checkins, match_to_set and its
//                 players' player_ranked_set deleted; dll_notifications:{player} (admin_banner)
// Redis, read     realtime:conn:{player} ip; connections:{player} nodePort (P2P); match_to_set:{match}, ranked_set:{set}
// Sent (ws:send)  GameServerReadyNotification, one per player; matchmaking-complete, one per party; the config
//                 (OnGameplayConfigNotified or the launch's template). Abandoned: matchmaking-cancel, one per player
//
// Unlike the TS websocket: UDP_SERVER_IP2 is not read (every deployment sets it to UDP_SERVER_IP; TS picked one of the
// two at random per match); the config is built before anyone is told, and a match without one is called off (TS told
// the players to connect, then built the config: a build that failed left them waiting for a match that never came).

/// <summary>Announcing a match that was just made (see the header of MatchLaunches.cs).</summary>
public static class MatchLaunches
{
    public const string Stream = "match:launched";
    public const string GameServerReadyTemplate = "GameServerReadyNotification";
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(20);

    public const string CancelledTitle = "Match Cancelled";
    public const string CancelledMessage = "The server could not set up your match, so you're back in the lobby.";
    public const string ClosedMessage = "The server could not set up your match, so you'll be returned to the title screen.";
    public const string ClosedReason = "match-cancelled";

    /// <summary>How long a game that cannot be cancelled has to show the banner before its connection closes.</summary>
    public static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Keeps <paramref name="notification"/> at {match} and announces it with each party's matchmaking-complete: published
    /// and sent at once, or appended to match:launched with Realtime:Gateway on.
    /// </summary>
    public static async Task AnnounceAsync(IServiceProvider services, IDatabase redis, string matchId, string notification, IReadOnlyList<MatchComplete> complete)
    {
        await redis.StringSetAsync(matchId, notification, s_ttl);
        if (Gateway(services))
        {
            var parties = new JsonArray([.. complete.Select(c => (JsonNode)new JsonObject
            {
                ["playerIds"] = new JsonArray([.. c.PlayerIds.Select(id => (JsonNode)id)]),
                ["requestId"] = c.RequestId?.DeepClone(),
                ["searching"] = c.Searching,
            })]);
            await redis.StreamAddAsync(Stream, [new("match", matchId), new("notification", notification), new("complete", Js.Stringify(parties))],
                maxLength: 10_000, useApproximateMaxLength: true);
            return;
        }

        await redis.PublishAsync(RedisChannel.Literal(MatchLauncher.NotificationChannel), notification);
        foreach (var party in complete)
        {
            await PlayerMessages.SendAsync(redis, party.PlayerIds, MatchLauncher.MatchmakingComplete(matchId, party.RequestId, ObjectId.GenerateNewId().ToString()));
        }
    }

    /// <summary>Realtime:Gateway, as this executable reads it (off where the setting is not bound).</summary>
    public static bool Gateway(IServiceProvider services) => services.GetService<IOptionsMonitor<RealtimeSettings>>()?.CurrentValue.Gateway == true;

    /// <summary>GameServerReadyNotification for one player, as the TS websocket's handleMatchFound built it.</summary>
    public static JsonObject GameServerReady(string matchId, JsonNode? matchKey, JsonNode port, string address) =>
        PlayerMessages.Update(new JsonObject
        {
            ["MatchKey"] = matchKey?.DeepClone(),
            ["MatchID"] = matchId,
            ["Port"] = port,
            ["template_id"] = GameServerReadyTemplate,
            ["IPAddress"] = address,
        }, new JsonObject { ["match"] = new JsonObject { ["id"] = matchId } });

    /// <summary>An address the TS websocket took for "this machine": its loopback, either family.</summary>
    public static bool IsLoopback(string? ip) => ip is "127.0.0.1" or "::1";

    /// <summary>
    /// The port of the P2P node on a player's machine: the one their client reported (connections:{player} nodePort),
    /// else Rollback:P2PNodePort, the fixed port a node takes when it can (the TS nodePortFor).
    /// </summary>
    public static async Task<int> NodePortAsync(IDatabase redis, string playerId, RollbackSettings settings)
    {
        int node = P2P.ParseNodePort((string?)await redis.HashGetAsync($"connections:{playerId}", "nodePort"));
        return node > 0 ? node : settings.P2PNodePort;
    }

    /// <summary>game-server-instance-ready for one player, as the TS websocket's handleGameServerInstanceReady built it.</summary>
    public static JsonObject GameServerInstanceReady(string matchId, string resultId, JsonNode port, string host) => new()
    {
        ["data"] = new JsonObject(),
        ["payload"] = new JsonObject
        {
            ["game_server_instance"] = new JsonObject
            {
                ["game_server_type_slug"] = "multiplay",
                ["port"] = port,
                ["owner_id"] = matchId,
                ["host"] = host,
                ["id"] = resultId,
            },
            ["proxied_data"] = null,
        },
        ["header"] = "Your game server is ready to join.",
        ["cmd"] = "game-server-instance-ready",
    };

    /// <summary>
    /// A game on its loading screen whose match will not be played: nothing the server sends takes it out of there, so
    /// it is told why (the OpenVersus client's banner) and its connection, the one open now, is closed after
    /// <see cref="CloseAfter"/> (the game goes back to its title screen). A game that reconnects meanwhile is left alone;
    /// without a connection of the gateway's (the TS websocket's), whichever it has is closed.
    /// </summary>
    public static async Task CloseAfterBannerAsync(IDatabase redis, TimeProvider time, string playerId)
    {
        var request = new JsonObject { ["playerId"] = playerId };
        if ((string?)await redis.HashGetAsync(GatewayPresence.ConnectionKey(playerId), "id") is { Length: > 0 } connectionId)
        {
            request["connectionId"] = connectionId;
        }

        request["code"] = 1000;
        request["reason"] = ClosedReason;
        await PlayerMessages.NotifyClientAsync(redis, playerId, "admin_banner", CancelledTitle, ClosedMessage,
            new JsonObject { ["timeout"] = 10 }, time.GetUtcNow().ToUnixTimeMilliseconds());
        await DelayedMessages.ScheduleDisconnectAsync(redis, time, request, CloseAfter);
    }
}

/// <summary>
/// A party's matchmaking-complete: its players, and the request it answers (null: none, the key left out).
/// <paramref name="Searching"/>: the request is the game's own (a matchmaker ticket), so the game is still searching
/// until it is told; a launcher's request is one it makes up for the game.
/// </summary>
public sealed record MatchComplete(IReadOnlyList<string> PlayerIds, JsonNode? RequestId, bool Searching = false);

/// <summary>Tells the players of each launched match about it (see the header of MatchLaunches.cs).</summary>
internal sealed class MatchLaunchStream(IServiceProvider services, IGameplayConfigs configs, IOptionsMonitor<RollbackSettings> rollback,
    TimeProvider time, ILogger<MatchLaunchStream> log) : BackgroundService
{
    public const string Group = "matchflow";
    private const int MaxDeliveries = 5;
    // Each match start waits up to this for its turn: small beside the game's own connect, and a read every 50 ms per
    // replica is nothing to Redis.
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_onceTtl = TimeSpan.FromMinutes(20);
    internal static TimeSpan ClaimAfter { get; set; } = TimeSpan.FromSeconds(10);
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Launched matches are not announced from here: this service has no Redis (REDIS)");
            return;
        }

        var lastClaim = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureGroupAsync(redis);
                int handled = await ReadAsync(redis, stoppingToken);
                if (time.GetUtcNow() - lastClaim > TimeSpan.FromSeconds(2))
                {
                    lastClaim = time.GetUtcNow();
                    handled += await ClaimAsync(redis, stoppingToken);
                }

                if (handled == 0)
                {
                    await Task.Delay(s_idle, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Launched matches: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
    }

    // At the start of the stream: a launch appended before any replica ever ran is read too, and one whose match is
    // over is acknowledged unsent ({match} gone).
    private bool _grouped;

    internal async Task EnsureGroupAsync(IDatabase redis)
    {
        if (_grouped)
        {
            return;
        }

        try
        {
            await redis.StreamCreateConsumerGroupAsync(MatchLaunches.Stream, Group, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }

        _grouped = true;
    }

    internal async Task<int> ReadAsync(IDatabase redis, CancellationToken ct)
    {
        StreamEntry[] entries;
        try
        {
            entries = await redis.StreamReadGroupAsync(MatchLaunches.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // The stream (and its group) went away (a flush, a failover with no persistence): made again, from its start.
            log.LogWarning("Launched matches: the stream's consumer group is gone; made again");
            _grouped = false;
            await EnsureGroupAsync(redis);
            entries = await redis.StreamReadGroupAsync(MatchLaunches.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }

        foreach (var entry in entries)
        {
            await HandleAsync(redis, entry, ct);
        }

        return entries.Length;
    }

    // Launches another consumer left pending: announced here, or dropped after too many deliveries.
    internal async Task<int> ClaimAsync(IDatabase redis, CancellationToken ct)
    {
        var claimed = await redis.StreamAutoClaimAsync(MatchLaunches.Stream, Group, _consumer, (long)ClaimAfter.TotalMilliseconds, "0-0", 50);
        foreach (var entry in claimed.ClaimedEntries)
        {
            var pending = await redis.StreamPendingMessagesAsync(MatchLaunches.Stream, Group, 1, _consumer, entry.Id, entry.Id);
            if (pending.Length > 0 && pending[0].DeliveryCount > MaxDeliveries)
            {
                log.LogError("Launched match {Match} failed {Count} times; dropped, its players were not told", (string?)entry["match"], pending[0].DeliveryCount);
                await redis.StreamAcknowledgeAsync(MatchLaunches.Stream, Group, entry.Id);
                continue;
            }

            await HandleAsync(redis, entry, ct);
        }

        return claimed.ClaimedEntries.Length;
    }

    private async Task HandleAsync(IDatabase redis, StreamEntry entry, CancellationToken ct)
    {
        try
        {
            await AnnounceAsync(redis, (string?)entry["match"] ?? "", (string?)entry["notification"], (string?)entry["complete"], ct);
            await redis.StreamAcknowledgeAsync(MatchLaunches.Stream, Group, entry.Id);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogError("Launched match {Match} not announced yet (it stays pending): {Error}", (string?)entry["match"], e.Message);
        }
    }

    internal async Task AnnounceAsync(IDatabase redis, string matchId, string? json, string? completeJson, CancellationToken ct)
    {
        if (json is null || Js.Parse(json) is not JsonObject notification)
        {
            log.LogError("Launched match {Match}: its notification is not a JSON object; nobody is told", matchId);
            return;
        }

        if (!await redis.KeyExistsAsync(matchId))
        {
            log.LogWarning("Launched match {Match} is over ({{match}} expired) before it was announced; nobody is told", matchId);
            return;
        }

        JsonObject? config = null;
        try
        {
            config = await configs.BuildAsync(notification, GameplayConfigMode.On, ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or RedisException or TimeoutException))
        {
            log.LogError(e, "Match {Match}: its gameplay config failed", matchId);
        }

        if (!await redis.StringSetAsync($"match_announced:{matchId}", "1", s_onceTtl, When.NotExists))
        {
            log.LogInformation("Match {Match} was already announced", matchId);
            return;
        }

        var players = (notification["players"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(p => !RollbackCallbacks.Truthy(p["isBot"]))
            .Select(p => RollbackCallbacks.Text(p["playerId"]))
            .OfType<string>()
            .ToList();
        var parties = (completeJson is null ? null : Js.Parse(completeJson) as JsonArray ?? [])?.OfType<JsonObject>()
            .Select(c => new MatchComplete([.. (c["playerIds"] as JsonArray ?? []).Select(RollbackCallbacks.Text).OfType<string>()], c["requestId"],
                RollbackCallbacks.Truthy(c["searching"])))
            .ToList() ?? [];
        if (config is null)
        {
            await AbandonAsync(redis, matchId, players, parties);
            return;
        }

        bool p2p = RollbackCallbacks.Truthy(notification["p2p"]);
        var settings = rollback.CurrentValue;
        foreach (string playerId in players)
        {
            var (address, port) = await GameServerAsync(redis, playerId, notification, p2p, settings);
            await PlayerMessages.SendAsync(redis, [playerId], MatchLaunches.GameServerReady(matchId, notification["matchKey"], port, address));
            log.LogInformation("Match {Match} found for player {Player}: game server {Address}:{Port}{P2P}", matchId, playerId, address, port.ToJsonString(),
                p2p ? " (P2P node)" : "");
        }

        foreach (var party in parties)
        {
            await PlayerMessages.SendAsync(redis, party.PlayerIds, MatchLauncher.MatchmakingComplete(matchId, party.RequestId, ObjectId.GenerateNewId().ToString()));
        }

        await PlayerMessages.SendAsync(redis, players, config);
    }

    // A match with no config is never played: it goes (and a set it was a game of), and its players' games are sent back
    // to the lobby, with a word on why.
    private async Task AbandonAsync(IDatabase redis, string matchId, IReadOnlyList<string> players, IReadOnlyList<MatchComplete> parties)
    {
        if ((string?)await redis.StringGetAsync($"match_to_set:{matchId}") is not { Length: > 0 } setId)
        {
            setId = await redis.KeyExistsAsync($"ranked_set:{matchId}") ? matchId : null;
        }

        if (setId is not null)
        {
            var members = Js.Parse((string?)await redis.StringGetAsync($"ranked_set:{setId}") ?? "null")?["players"] as JsonArray ?? [];
            foreach (string member in members.Select(m => RollbackCallbacks.Text(m?["playerId"])).OfType<string>().Concat(players).Distinct())
            {
                if ((string?)await redis.StringGetAsync($"player_ranked_set:{member}") == setId)
                {
                    await redis.KeyDeleteAsync($"player_ranked_set:{member}");
                }
            }

            await redis.KeyDeleteAsync([$"ranked_set:{setId}", $"ranked_set_checkins:{setId}", $"match_to_set:{matchId}"]);
        }

        await redis.KeyDeleteAsync([(RedisKey)matchId, $"match:{matchId}"]);
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var closed = new List<string>();
        foreach (string playerId in players)
        {
            if (parties.FirstOrDefault(p => p.Searching && p.PlayerIds.Contains(playerId)) is { RequestId: { } requestId })
            {
                await PlayerMessages.SendAsync(redis, [playerId], MatchLauncher.MatchmakingCancelled(requestId.DeepClone()));
                await PlayerMessages.NotifyClientAsync(redis, playerId, "admin_banner", MatchLaunches.CancelledTitle, MatchLaunches.CancelledMessage,
                    new JsonObject { ["timeout"] = 10 }, now);
                continue;
            }

            await MatchLaunches.CloseAfterBannerAsync(redis, time, playerId);
            closed.Add(playerId);
        }

        log.LogError("Match {Match} has no gameplay config: called off{Set}; sent back to the lobby: {Lobby}; connections closed in {Seconds} s: {Closed}",
            matchId, setId is null ? "" : $" with its set {setId}", string.Join(", ", players.Except(closed)), MatchLaunches.CloseAfter.TotalSeconds, string.Join(", ", closed));
    }

    // Where one player's game connects: their own node in a P2P match, else the rollback server (loopback for a player
    // on this machine).
    private async Task<(string Address, JsonNode Port)> GameServerAsync(IDatabase redis, string playerId, JsonObject notification, bool p2p, RollbackSettings settings)
    {
        if (p2p)
        {
            return ("127.0.0.1", await MatchLaunches.NodePortAsync(redis, playerId, settings));
        }

        JsonNode port;
        if (RollbackCallbacks.Truthy(notification["rollbackPort"]))
        {
            port = notification["rollbackPort"]!.DeepClone();
        }
        else
        {
            port = settings.UdpPort;
            log.LogWarning("Match {Match} names no rollback port; GameServerReadyNotification names Rollback:UdpPort (UDP_PORT) {Port}",
                RollbackCallbacks.Text(notification["matchId"]), settings.UdpPort);
        }

        if (MatchLaunches.IsLoopback((string?)await redis.HashGetAsync(GatewayPresence.ConnectionKey(playerId), "ip")))
        {
            return ("127.0.0.1", port);
        }

        if (settings.UdpServerIp.Length == 0)
        {
            log.LogError("No Rollback:UdpServerIp (UDP_SERVER_IP): player {Player} is sent no rollback server address", playerId);
        }

        return (settings.UdpServerIp, port);
    }
}

public static class MatchLaunchesHosting
{
    /// <summary>The match flow's reader of match:launched (Realtime:Gateway on); needs IGameplayConfigs (AddGameplayConfigs).</summary>
    public static WebApplicationBuilder AddMatchLaunches(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RollbackSettings>("Rollback");
        builder.AddSetting<RealtimeSettings>("Realtime");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<MatchLaunchStream>();
        return builder;
    }
}
