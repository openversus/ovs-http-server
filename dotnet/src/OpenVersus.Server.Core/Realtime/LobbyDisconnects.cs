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
//   6. their session: connections:{player} and its match copy of the cosmetics, only while it is still the session the
//      closed connection was opened with (a script compares the token); then player:{player}* (found by SCAN: the TS
//      createLobby records player:{player}:lobby:{lobby} have no TTL), and the IP's copy of the session
//      (connections:{ip}) while it is still this player's. The match flow reads the session too (a pregame dodge's
//      fighter, after match_characters:{set}; GameplayConfigs at a match's start): TS deleted it at the close as well.
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
//                 connections:{player} (jwt, current_ip); connections:{ip} (id); rejoin_pending:{player} (its TTL)
// Redis, written  realtime:disconnects:due (ZSET: score the time it is due in ms, member the event as JSON); online_players
//                 (SREM); connections:{player}, connections:{player}:cosmetics, player:{player}*, connections:{ip} (DEL);
//                 the party's, the custom lobby's and the queue's writes (PartyService, CustomLobbyService,
//                 MatchmakingQueue)
//
// A disconnect the gateway made for a node that died (reaped, GatewayReaper) is handled as any other. One that names no
// session token (the player had no connection entry left to read it from) keeps the session: nothing tells the closed
// connection's session from a login since. The match is the match flow's reader's (MatchDisconnects).
//
// Unlike the TS websocket:
//   - a close in the post-match window is handled when the window closes, as any other (the other party member is
//     told); TS skipped it, and when its 45 s timer ran only took the player out of the lobby, without telling anyone.
//   - the custom lobby is found by the player's ssc_custom_lobby_player, then by a SCAN when that is gone; TS read every
//     stored custom lobby (KEYS) on every disconnect. The leave script is C#'s (CustomLobbyService's header).
//   - the web custom lobby (custom_lobby_player:{player}) is not left: its pages were retired (docs/REALTIME.md).
//   - the queue: see MatchmakingQueue's header (the rest of the party is cancelled and told; any list, Casual included).
//   - the session goes first, and only while it is the closed connection's; player:{player}* and the IP's copy only
//     after it (a login since writes player:{player}:blocked). TS deleted player:{player}* first, whatever happened.
//   - the IP's copy is the one /access wrote (the session's current_ip; the websocket's own address when there is none),
//     and that key alone; TS took the websocket's address, which need not be the one /access saw (the bench: ::1 against
//     127.0.0.1), and deleted connections:{ip}* (so 1.2.3.4 took 1.2.3.40's copy too).

/// <summary>
/// A disconnected event: the player, the connection that closed, the hash of its session token, when it closed (ms), and
/// how often handling it failed.
/// </summary>
internal sealed record Disconnect(string PlayerId, string ConnectionId, string TokenHash, long At, int Failures = 0, string Ip = "")
{
    public string ToJson() => Js.Stringify(new JsonObject
    {
        ["player"] = PlayerId,
        ["connection"] = ConnectionId,
        ["token"] = TokenHash,
        ["at"] = At,
        ["ip"] = Ip,
        ["failures"] = Failures,
    });

    public static Disconnect? FromJson(string json) =>
        Js.Parse(json) is JsonObject o && o["player"]?.GetValue<string>() is { Length: > 0 } player
            ? new Disconnect(player, o["connection"]?.GetValue<string>() ?? "", o["token"]?.GetValue<string>() ?? "", o["at"]?.GetValue<long>() ?? 0,
                o["failures"]?.GetValue<int>() ?? 0, o["ip"]?.GetValue<string>() ?? "")
            : null;
}

/// <summary>The lobbies' reader of the realtime gateway's disconnects (see the header of LobbyDisconnects.cs).</summary>
internal sealed class LobbyDisconnects(IServiceProvider services, IPartyService party, ICustomLobbyService customLobbies, TimeProvider time,
    ILogger<LobbyDisconnects> logger) : ConnectionEventsReader(services, time, logger)
{
    public const string GroupName = "lobbies";
    public const string DueSet = "realtime:disconnects:due";
    private const int MaxFailures = 5;
    private static readonly TimeSpan s_retry = TimeSpan.FromSeconds(5);

    protected override string Group => GroupName;

    // The deferred disconnects, a test's own in tests.
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

    // The session and its cosmetics copy, while its token is still ARGV[1] ('' for a session with none); 1 when it was.
    private const string SessionScript = """
        if (redis.call('HGET', KEYS[1], 'jwt') or '') ~= ARGV[1] then
          return 0
        end
        for _, key in ipairs(KEYS) do
          if redis.call('EXISTS', key) == 1 then
            redis.call('DEL', key)
          end
        end
        return 1
        """;

    // The IP's copy of a session, while it is still this player's.
    private const string IpCopyScript = """
        if redis.call('HGET', KEYS[1], 'id') == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    internal enum Outcome { Done, Deferred, Back }

    protected override async Task OnEventAsync(IDatabase redis, ConnectionEvent connectionEvent)
    {
        switch (connectionEvent.Type)
        {
            // No time (never written so): no ticket is older than it, so none is dropped.
            case "disconnected":
                await DisconnectedAsync(redis, new Disconnect(connectionEvent.PlayerId, connectionEvent.ConnectionId, connectionEvent.TokenHash,
                    connectionEvent.At, Ip: connectionEvent.Ip));
                break;
            case "replaced" when await MatchmakingQueue.DropAsync(redis, connectionEvent.PlayerId, connectionEvent.At):
                Log.LogInformation("Dropped the ticket of {Player}'s replaced connection {Connection}", connectionEvent.PlayerId, connectionEvent.ConnectionId);
                break;
        }
    }

    protected override Task<int> EverySecondAsync(IDatabase redis) => SweepAsync(redis);

    // The deferred disconnects that are due: handled, or put back for later when handling fails.
    internal async Task<int> SweepAsync(IDatabase redis)
    {
        var taken = (RedisResult[]?)await redis.ScriptEvaluateAsync(TakeDueScript, [DueKey], [Time.GetUtcNow().ToUnixTimeMilliseconds()]) ?? [];
        foreach (var member in taken)
        {
            if (Disconnect.FromJson((string)member!) is not { } disconnect)
            {
                Log.LogError("Disconnects: a deferred entry that is not one was dropped: {Entry}", (string?)member);
                continue;
            }

            try
            {
                await DisconnectedAsync(redis, disconnect);
            }
            catch (Exception e)
            {
                if (disconnect.Failures + 1 >= MaxFailures)
                {
                    Log.LogError(e, "Disconnect of {Player} failed {Count} times; dropped, their lobbies were not cleaned up", disconnect.PlayerId, disconnect.Failures + 1);
                    continue;
                }

                Log.LogError("Disconnect of {Player} not cleaned up yet (tried again in {Seconds} s): {Error}", disconnect.PlayerId, s_retry.TotalSeconds, e.Message);
                await redis.SortedSetAddAsync(DueKey, (disconnect with { Failures = disconnect.Failures + 1 }).ToJson(),
                    (Time.GetUtcNow() + s_retry).ToUnixTimeMilliseconds());
            }
        }

        return taken.Length;
    }

    /// <summary>One disconnect, now or when the post-match window closes (see the header).</summary>
    internal async Task<Outcome> DisconnectedAsync(IDatabase redis, Disconnect disconnect)
    {
        string player = disconnect.PlayerId;
        var (back, token) = await ConnectionEvents.BackAsync(redis, player, disconnect.ConnectionId, disconnect.TokenHash);
        if (back is not null)
        {
            await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
            Log.LogInformation("Disconnect of {Player} ({Connection}) not cleaned up but for the ticket: {Why}", player, disconnect.ConnectionId, back);
            return Outcome.Back;
        }

        if (await redis.KeyTimeToLiveAsync($"rejoin_pending:{player}") is { } window && window > TimeSpan.Zero)
        {
            await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
            await redis.SortedSetAddAsync(DueKey, disconnect.ToJson(), (Time.GetUtcNow() + window).ToUnixTimeMilliseconds());
            Log.LogInformation("Disconnect of {Player}: their lobbies wait for the end of their post-match window ({Seconds:0.#} s)", player, window.TotalSeconds);
            return Outcome.Deferred;
        }

        await party.PlayerDisconnectedAsync(player);
        await customLobbies.PlayerDisconnectedAsync(player);
        await MatchmakingQueue.DropAsync(redis, player, disconnect.At);
        await redis.ScriptEvaluateAsync(OfflineScript, [GatewayPresence.ConnectionKey(player), GatewayPresence.OnlinePlayers], [player]);
        await party.ForgetLobbyAsync(player);
        bool session = await ForgetSessionAsync(redis, disconnect, token);
        Log.LogInformation("Player {Player} disconnected ({Connection}): lobbies cleaned up{Session}", player, disconnect.ConnectionId,
            session ? ", session deleted" : "; the session is a newer login's, kept");
        return Outcome.Done;
    }

    // The TS close's redisDeletePlayerKeys and IP record (websocket.ts 691-704), the session first (see the header).
    private async Task<bool> ForgetSessionAsync(IDatabase redis, Disconnect disconnect, string? token)
    {
        if (disconnect.TokenHash.Length == 0)
        {
            return false;
        }

        string player = disconnect.PlayerId;
        string ip = (string?)await redis.HashGetAsync($"connections:{player}", "current_ip") is { Length: > 0 } current ? current : disconnect.Ip;
        if ((long)await redis.ScriptEvaluateAsync(SessionScript, [$"connections:{player}", $"connections:{player}:cosmetics"], [token ?? ""]) != 1)
        {
            return false;
        }

        if (Services.GetService<IConnectionMultiplexer>() is { } multiplexer)
        {
            var keys = new List<RedisKey>();
            await foreach (var key in RedisScan.KeysAsync(multiplexer, redis.Database, $"player:{player}*"))
            {
                keys.Add(key);
            }

            if (keys.Count > 0)
            {
                await redis.KeyDeleteAsync([.. keys]);
            }
        }

        if (ip.Length > 0)
        {
            await redis.ScriptEvaluateAsync(IpCopyScript, [$"connections:{ip}"], [player]);
        }

        return true;
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
