using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// Who is connected, and where (the realtime gateway; docs/REALTIME.md). Any number of gateway nodes hold the game's
// sockets; a player has one current connection, named in realtime:conn:{player}. Only the current connection changes
// the player's presence: a connection a newer one replaced (another login, on this node or another) closes without
// touching it, so a reconnect is never undone by the old socket's close. Each connection's id is minted where the
// game's socket is held, and carried through every event, so a consumer can tell "this connection" from "the player".
//
// Redis, written  realtime:conn:{player} (hash: id, node, ip, at ms; PEXPIRE 3 min, renewed by every answer to the ping)
//                 online_players (SADD at the handshake; SREM when the current connection closes)
//                 player_heartbeats (ZADD ms at the handshake and every answer; ZREM when the current connection closes)
//                 active_ip_accounts:{ip} (the TS redisTouchPlayerSession at the handshake and every answer; ZREM at the close)
//                 realtime:connections (XADD, MAXLEN ~10,000; fields type, player, connection, at: connected and
//                 disconnected also node, ip, token (its SHA-256); replaced also replacedBy)
// Redis, read     rejoin_pending:{player} (MatchEnd: while it lives, a close keeps the player online and their IP's
//                 session, as the TS websocket's pendingRejoin did, for the party's rejoin, which reads online_players;
//                 what happens at its expiry is the disconnect consumer's. The heartbeat goes all the same, unlike TS:
//                 only the matchmaker reads it, and the game keeps one socket for its whole session, so a close in that
//                 window is a game that is gone, whose party's ticket is dropped at once instead of 41 s later)
// Published       ws:disconnect {playerId, except, code, reason} when a connection replaces one (the node holding the
//                 old one closes it; the TS websocket left a replaced socket open and stopped pinging it)
//
// Unlike the TS websocket: presence (online_players, the heartbeat, the IP's session) goes at the close itself, before
// anything else; TS removed online_players last, after a pre-game dodge's rating, and the match flow's rollback
// PlayerDisconnect calls a player still in online_players a rollback crash (MatchStatusEvents). The heartbeat goes at
// once as it did in TS: the matchmaker drops a ticket whose player has none. Nothing is cleared at startup (another node
// holds its own players).

/// <summary>The gateway's writes for one connection's handshake, answers to the ping, and close.</summary>
public static class GatewayPresence
{
    public const string ConnectionsStream = "realtime:connections";
    public const string OnlinePlayers = "online_players";
    public const string Heartbeats = "player_heartbeats";

    public static string ConnectionKey(string playerId) => $"realtime:conn:{playerId}";

    /// <summary>How long a connection's entry lives without an answer to the ping (several cut-offs: a node that died).</summary>
    public static readonly TimeSpan ConnectionTtl = TimeSpan.FromMinutes(3);

    // The TS server's ACTIVE_SESSION_TTL_MS and the key's EXPIRE (redisTouchPlayerSession, as AccessService at login).
    private static readonly TimeSpan s_activeSession = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_activeIpTtl = TimeSpan.FromSeconds(180);

    // The connection becomes the player's current one; returns the id of the one it replaced, or nil.
    private const string ClaimScript = """
        local old = redis.call('HGET', KEYS[1], 'id')
        redis.call('HSET', KEYS[1], 'id', ARGV[1], 'node', ARGV[2], 'ip', ARGV[3], 'at', ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return old
        """;

    // Only the current connection lets the player go: 1 when it was the current one.
    private const string ReleaseScript = """
        if redis.call('HGET', KEYS[1], 'id') == ARGV[1] then
          redis.call('DEL', KEYS[1])
          return 1
        end
        return 0
        """;

    /// <summary>
    /// A connection completed its handshake: it becomes the player's current one (a connection it replaces is asked to
    /// close, wherever it is), and the player is online. Returns the replaced connection's id, if any.
    /// </summary>
    public static async Task<string?> ConnectedAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        var replaced = (string?)await redis.ScriptEvaluateAsync(ClaimScript, [ConnectionKey(connection.PlayerId)],
            [connection.Id, connection.Node, connection.Ip, ms, (long)ConnectionTtl.TotalMilliseconds]);
        if (replaced == connection.Id)
        {
            replaced = null;
        }

        await redis.SortedSetAddAsync(Heartbeats, connection.PlayerId, ms);
        await redis.SetAddAsync(OnlinePlayers, connection.PlayerId);
        await TouchSessionAsync(redis, connection, ms);
        if (replaced is not null)
        {
            await AppendAsync(redis, "replaced", connection.PlayerId, replaced, ms, [new("replacedBy", connection.Id)]);
            await redis.PublishAsync(RedisChannel.Literal(GatewayChannels.Disconnect), Js.Stringify(new JsonObject
            {
                ["playerId"] = connection.PlayerId,
                ["except"] = connection.Id,
                ["code"] = 1000,
                ["reason"] = "replaced",
            }));
        }

        await AppendAsync(redis, "connected", connection, ms);
        return replaced;
    }

    /// <summary>The game answered the ping on its current connection: it is still there.</summary>
    public static async Task AnsweredAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        await redis.SortedSetAddAsync(Heartbeats, connection.PlayerId, ms);
        await TouchSessionAsync(redis, connection, ms);
        await redis.KeyExpireAsync(ConnectionKey(connection.PlayerId), ConnectionTtl);
    }

    /// <summary>
    /// A connection closed. Only the player's current connection takes them offline (and not while a post-match rejoin
    /// is pending); true when it was the current one, and a disconnected event was appended.
    /// </summary>
    public static async Task<bool> ClosedAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now)
    {
        if ((long)await redis.ScriptEvaluateAsync(ReleaseScript, [ConnectionKey(connection.PlayerId)], [connection.Id]) != 1)
        {
            return false;
        }

        await redis.SortedSetRemoveAsync(Heartbeats, connection.PlayerId);
        if (!await redis.KeyExistsAsync($"rejoin_pending:{connection.PlayerId}"))
        {
            await redis.SetRemoveAsync(OnlinePlayers, connection.PlayerId);
            if (connection.Ip.Length > 0)
            {
                await redis.SortedSetRemoveAsync($"active_ip_accounts:{connection.Ip}", connection.PlayerId);
            }
        }

        await AppendAsync(redis, "disconnected", connection, now.ToUnixTimeMilliseconds());
        return true;
    }

    /// <summary>The token's SHA-256 (hex): an event names the session a connection was opened with without carrying it.</summary>
    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // The IP's live sessions (the TS redisTouchPlayerSession): the old ones dropped, this one renewed.
    private static async Task TouchSessionAsync(IDatabase redis, GatewayConnectionInfo connection, long ms)
    {
        if (connection.Ip.Length == 0)
        {
            return;
        }

        var session = redis.CreateTransaction();
        string active = $"active_ip_accounts:{connection.Ip}";
        _ = session.SortedSetRemoveRangeByScoreAsync(active, 0, ms - s_activeSession.TotalMilliseconds);
        _ = session.SortedSetAddAsync(active, connection.PlayerId, ms);
        _ = session.KeyExpireAsync(active, s_activeIpTtl);
        await session.ExecuteAsync();
    }

    // connected and disconnected name the connection's node, IP and session (its token's hash); replaced names the
    // connection that replaced it.
    private static Task AppendAsync(IDatabase redis, string type, GatewayConnectionInfo connection, long ms) =>
        AppendAsync(redis, type, connection.PlayerId, connection.Id, ms,
            [new("node", connection.Node), new("ip", connection.Ip), new("token", connection.TokenHash)]);

    private static Task AppendAsync(IDatabase redis, string type, string playerId, string connectionId, long ms, NameValueEntry[] fields) =>
        redis.StreamAddAsync(ConnectionsStream,
            [new("type", type), new("player", playerId), new("connection", connectionId), new("at", ms), .. fields],
            maxLength: 10_000, useApproximateMaxLength: true);
}

/// <summary>
/// One game connection: its id (minted where the socket is held), the player, the node holding it, the client's IP (as
/// the reverse proxy reports it), and the hash of the session token it was opened with (<see cref="GatewayPresence.TokenHash"/>).
/// </summary>
public sealed record GatewayConnectionInfo(string Id, string PlayerId, string Node, string Ip, string TokenHash);

/// <summary>The channels every gateway node hears.</summary>
public static class GatewayChannels
{
    /// <summary>{playerIds, message}: each node sends message to the players whose current connection it holds.</summary>
    public const string Send = ProfileNotifications.WsSendChannel;

    /// <summary>
    /// {playerId, connectionId?, except?, code?, reason?}: the node holding the player's connection closes it, only if it
    /// is <c>connectionId</c> and is not <c>except</c> when those are given; with a code, a close handshake with that
    /// code and reason, without one at once (the ops command's, as the TS websocket's terminate()).
    /// </summary>
    public const string Disconnect = Ops.OpsService.DisconnectChannel;
}
