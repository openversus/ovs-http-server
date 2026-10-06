using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// What a player's disconnect does to their lobbies: the lobbies' part of the TS websocket's close (handleDisconnect,
// websocket.ts 388-709). The realtime gateway appends a disconnected event to realtime:connections when a player's current
// connection closes (GatewayPresence); the lobbies service reads them as the consumer group "lobbies" (each event handled
// once, whatever the number of replicas) and, in the TS order:
//   1. the party (IPartyService.PlayerDisconnectedAsync): a party of two loses the player; the other is told.
//   2. the custom lobby (ICustomLobbyService.PlayerDisconnectedAsync): out of it as leave_player_lobby takes a player.
//   3. the queue (MatchmakingQueue.DropAsync): the ticket the player held leaves its list, their tick stops, and the rest
//      of their party is cancelled and told.
//   4. online_players, unless they are connected again (the gateway keeps it through the post-match window, below).
//   5. their own lobby's records (IPartyService.ForgetLobbyAsync: player_lobby, and a solo lobby).
// A replaced event (a newer login took the connection over) drops the ticket too, as the TS websocket's handshake did
// (dropReplacedTicket), and nothing else.
//
// An event is about a connection, and acting on it later must not undo a return. The lobbies are left alone when the
// player has connected again (realtime:conn:{player}: the gateway deletes it when the current connection closes, so any
// entry is a newer one) or has logged in again (connections:{player} holds another session's token: /access comes before
// the websocket, and the event carries the hash of the token its connection was opened with). The ticket goes whatever
// happened since, unless it was queued after the event: a game that logs in again is not searching any more.
//
// The post-match window (rejoin_pending:{player}, MatchEnd: 45 s in which a party is kept for its players) defers the
// lobbies: the event waits in realtime:disconnects:due until the window closes, then is handled as any other, unless the
// player came back meanwhile. The ticket goes at once (the gateway drops the heartbeat at the close for the same reason).
//
// Redis, read     realtime:connections (XREADGROUP, group "lobbies", made at the stream's end: events appended before
//                 the group existed are not replayed against players who are back); realtime:conn:{player} (id);
//                 connections:{player} (jwt); rejoin_pending:{player} (its TTL)
// Redis, written  realtime:disconnects:due (ZSET: score the time it is due in ms, member the event as JSON); online_players
//                 (SREM); the party's, the custom lobby's and the queue's writes (PartyService, CustomLobbyService,
//                 MatchmakingQueue)
//
// Not yet (slice 3d): the session keys (connections:{player}*, player:{player}*, the IP's copy), the match (a dodge, the
// ranked set's flag: the match flow's reader), connected (the daily toast), and the players of a gateway node that died
// without closing its connections.
//
// Unlike the TS websocket:
//   - a close in the post-match window is handled when the window closes, as any other (the other party member is
//     told); TS skipped it, and when its 45 s timer ran only took the player out of the lobby, without telling anyone.
//   - the custom lobby is found by the player's ssc_custom_lobby_player, then by a SCAN when that is gone; TS read every
//     stored custom lobby (KEYS) on every disconnect. The leave script is C#'s (CustomLobbyService's header).
//   - the web custom lobby (custom_lobby_player:{player}) is not left: its pages were retired (docs/REALTIME.md).
//   - the queue: see MatchmakingQueue's header (the rest of the party is cancelled and told; any list, Casual included).

/// <summary>
/// A disconnected event: the player, the connection that closed, the hash of its session token, when it closed (ms), and
/// how often handling it failed.
/// </summary>
internal sealed record Disconnect(string PlayerId, string ConnectionId, string TokenHash, long At, int Failures = 0)
{
    public string ToJson() => Js.Stringify(new JsonObject
    {
        ["player"] = PlayerId,
        ["connection"] = ConnectionId,
        ["token"] = TokenHash,
        ["at"] = At,
        ["failures"] = Failures,
    });

    public static Disconnect? FromJson(string json) =>
        Js.Parse(json) is JsonObject o && o["player"]?.GetValue<string>() is { Length: > 0 } player
            ? new Disconnect(player, o["connection"]?.GetValue<string>() ?? "", o["token"]?.GetValue<string>() ?? "", o["at"]?.GetValue<long>() ?? 0,
                o["failures"]?.GetValue<int>() ?? 0)
            : null;
}

/// <summary>The lobbies' reader of the realtime gateway's disconnects (see the header of LobbyDisconnects.cs).</summary>
internal sealed class LobbyDisconnects(IServiceProvider services, IPartyService party, ICustomLobbyService customLobbies, TimeProvider time,
    ILogger<LobbyDisconnects> log) : BackgroundService
{
    public const string Group = "lobbies";
    public const string DueSet = "realtime:disconnects:due";
    private const int MaxDeliveries = 5;
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_retry = TimeSpan.FromSeconds(5);
    internal static TimeSpan ClaimAfter { get; set; } = TimeSpan.FromSeconds(10);
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    // The keys, a test's own in tests (the gateway's tests append to the stream in the same database).
    internal string StreamKey { get; init; } = GatewayPresence.ConnectionsStream;
    internal string DueKey { get; init; } = DueSet;

    // Up to 20 deferred disconnects whose time has come, each taken by one replica.
    private const string TakeDueScript = """
        local due = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, 20)
        for _, member in ipairs(due) do
          redis.call('ZREM', KEYS[1], member)
        end
        return due
        """;

    // Out of online_players, unless connected again (and only when still there: the gateway removes them at the close
    // but in a post-match window).
    private const string OfflineScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 or redis.call('SISMEMBER', KEYS[2], ARGV[1]) == 0 then
          return 0
        end
        return redis.call('SREM', KEYS[2], ARGV[1])
        """;

    internal enum Outcome { Done, Deferred, Back }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Disconnects are not cleaned up from here: this service has no Redis (REDIS)");
            return;
        }

        DateTimeOffset lastClaim = DateTimeOffset.MinValue, lastSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureGroupAsync(redis);
                int handled = await ReadAsync(redis);
                if (time.GetUtcNow() - lastClaim > TimeSpan.FromSeconds(2))
                {
                    lastClaim = time.GetUtcNow();
                    handled += await ClaimAsync(redis);
                }

                if (time.GetUtcNow() - lastSweep > TimeSpan.FromSeconds(1))
                {
                    lastSweep = time.GetUtcNow();
                    handled += await SweepAsync(redis);
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
                log.LogError(e, "Disconnects: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
    }

    private bool _grouped;

    internal async Task EnsureGroupAsync(IDatabase redis)
    {
        if (_grouped)
        {
            return;
        }

        try
        {
            await redis.StreamCreateConsumerGroupAsync(StreamKey, Group, StreamPosition.NewMessages, createStream: true);
        }
        catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }

        _grouped = true;
    }

    internal async Task<int> ReadAsync(IDatabase redis)
    {
        StreamEntry[] entries;
        try
        {
            entries = await redis.StreamReadGroupAsync(StreamKey, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // The stream (and its group) went away (a flush, a failover with no persistence): made again, at its end.
            log.LogWarning("Disconnects: the stream's consumer group is gone; made again");
            _grouped = false;
            await EnsureGroupAsync(redis);
            entries = await redis.StreamReadGroupAsync(StreamKey, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }

        foreach (var entry in entries)
        {
            await HandleAsync(redis, entry);
        }

        return entries.Length;
    }

    // Events another consumer left pending: handled here, or dropped after too many deliveries.
    internal async Task<int> ClaimAsync(IDatabase redis)
    {
        var claimed = await redis.StreamAutoClaimAsync(StreamKey, Group, _consumer, (long)ClaimAfter.TotalMilliseconds, "0-0", 50);
        foreach (var entry in claimed.ClaimedEntries)
        {
            var pending = await redis.StreamPendingMessagesAsync(StreamKey, Group, 1, _consumer, entry.Id, entry.Id);
            if (pending.Length > 0 && pending[0].DeliveryCount > MaxDeliveries)
            {
                log.LogError("Disconnect of {Player} failed {Count} times; dropped, their lobbies were not cleaned up", (string?)entry["player"], pending[0].DeliveryCount);
                await redis.StreamAcknowledgeAsync(StreamKey, Group, entry.Id);
                continue;
            }

            await HandleAsync(redis, entry);
        }

        return claimed.ClaimedEntries.Length;
    }

    private async Task HandleAsync(IDatabase redis, StreamEntry entry)
    {
        try
        {
            if ((string?)entry["player"] is { Length: > 0 } player)
            {
                // No time (never written so): no ticket is older than it, so none is dropped.
                long at = (long?)entry["at"] ?? 0;
                switch ((string?)entry["type"])
                {
                    case "disconnected":
                        await DisconnectedAsync(redis, new Disconnect(player, (string?)entry["connection"] ?? "", (string?)entry["token"] ?? "", at));
                        break;
                    case "replaced" when await MatchmakingQueue.DropAsync(redis, player, at):
                        log.LogInformation("Dropped the ticket of {Player}'s replaced connection {Connection}", player, (string?)entry["connection"]);
                        break;
                }
            }

            await redis.StreamAcknowledgeAsync(StreamKey, Group, entry.Id);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogError("Disconnect of {Player} not cleaned up yet (it stays pending): {Error}", (string?)entry["player"], e.Message);
        }
    }

    // The deferred disconnects that are due: handled, or put back for later when handling fails.
    internal async Task<int> SweepAsync(IDatabase redis)
    {
        var taken = (RedisResult[]?)await redis.ScriptEvaluateAsync(TakeDueScript, [DueKey], [time.GetUtcNow().ToUnixTimeMilliseconds()]) ?? [];
        foreach (var member in taken)
        {
            if (Disconnect.FromJson((string)member!) is not { } disconnect)
            {
                log.LogError("Disconnects: a deferred entry that is not one was dropped: {Entry}", (string?)member);
                continue;
            }

            try
            {
                await DisconnectedAsync(redis, disconnect);
            }
            catch (Exception e)
            {
                if (disconnect.Failures + 1 >= MaxDeliveries)
                {
                    log.LogError(e, "Disconnect of {Player} failed {Count} times; dropped, their lobbies were not cleaned up", disconnect.PlayerId, disconnect.Failures + 1);
                    continue;
                }

                log.LogError("Disconnect of {Player} not cleaned up yet (tried again in {Seconds} s): {Error}", disconnect.PlayerId, s_retry.TotalSeconds, e.Message);
                await redis.SortedSetAddAsync(DueKey, (disconnect with { Failures = disconnect.Failures + 1 }).ToJson(),
                    (time.GetUtcNow() + s_retry).ToUnixTimeMilliseconds());
            }
        }

        return taken.Length;
    }

    /// <summary>One disconnect, now or when the post-match window closes (see the header).</summary>
    internal async Task<Outcome> DisconnectedAsync(IDatabase redis, Disconnect disconnect)
    {
        string player = disconnect.PlayerId;
        if (await BackAsync(redis, disconnect) is { } back)
        {
            await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
            log.LogInformation("Disconnect of {Player} ({Connection}) not cleaned up but for the ticket: {Why}", player, disconnect.ConnectionId, back);
            return Outcome.Back;
        }

        if (await redis.KeyTimeToLiveAsync($"rejoin_pending:{player}") is { } window && window > TimeSpan.Zero)
        {
            await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
            await redis.SortedSetAddAsync(DueKey, disconnect.ToJson(), (time.GetUtcNow() + window).ToUnixTimeMilliseconds());
            log.LogInformation("Disconnect of {Player}: their lobbies wait for the end of their post-match window ({Seconds:0.#} s)", player, window.TotalSeconds);
            return Outcome.Deferred;
        }

        await party.PlayerDisconnectedAsync(player);
        await customLobbies.PlayerDisconnectedAsync(player);
        await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
        await redis.ScriptEvaluateAsync(OfflineScript, [GatewayPresence.ConnectionKey(player), GatewayPresence.OnlinePlayers], [player]);
        await party.ForgetLobbyAsync(player);
        log.LogInformation("Player {Player} disconnected ({Connection}): lobbies cleaned up", player, disconnect.ConnectionId);
        return Outcome.Done;
    }

    // Why the event no longer speaks for the player, or null.
    private static async Task<string?> BackAsync(IDatabase redis, Disconnect disconnect)
    {
        if ((string?)await redis.HashGetAsync(GatewayPresence.ConnectionKey(disconnect.PlayerId), "id") is { Length: > 0 } current
            && current != disconnect.ConnectionId)
        {
            return $"connected again ({current})";
        }

        if (disconnect.TokenHash.Length > 0
            && (string?)await redis.HashGetAsync($"connections:{disconnect.PlayerId}", "jwt") is { Length: > 0 } token
            && GatewayPresence.TokenHash(token) != disconnect.TokenHash)
        {
            return "logged in again";
        }

        return null;
    }
}

public static class LobbyDisconnectsHosting
{
    /// <summary>The lobbies' reader of the realtime gateway's disconnects; needs AddPartyLobbies and AddCustomLobbies.</summary>
    public static WebApplicationBuilder AddLobbyDisconnects(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<LobbyDisconnects>();
        return builder;
    }
}
