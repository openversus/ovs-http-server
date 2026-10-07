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
// Redis, written  realtime:conn:{player} (hash: id, node, ip, at ms, token (its SHA-256), attach (the socket holding it on
//                 that node: an edge's link moved to another node keeps the id, and only the socket holding it now
//                 releases it), edge (the edge instance the game comes through; empty: connected directly);
//                 PEXPIRE 3 min, renewed by every answer to the ping)
//                 online_players (SADD at the handshake; SREM when the current connection closes)
//                 player_heartbeats (ZADD ms at the handshake and every answer; ZREM when the current connection closes)
//                 active_ip_accounts:{ip} (the TS redisTouchPlayerSession at the handshake and every answer; ZREM at the close)
//                 realtime:connections (XADD, MAXLEN ~10,000; fields type, player, connection, at: connected and
//                 disconnected also node, ip, token (its SHA-256); replaced also replacedBy; a reaped disconnected
//                 also reaped "1", as is a close no game asked for: an edge's link that never came back; resumed
//                 (an edge's connection re-attached to a node: node, ip, token; no reader acts on it))
// Redis, read     rejoin_pending:{player} (MatchEnd: while it lives, a close keeps the player online and their IP's
//                 session, as the TS websocket's pendingRejoin did, for the party's rejoin, which reads online_players;
//                 at its expiry LobbyDisconnects takes a player who did not come back offline. The heartbeat goes all
//                 the same, unlike TS: only the matchmaker reads it, and the game keeps one socket for its whole
//                 session, so a close in that window is a game that is gone, whose party's ticket is dropped at once
//                 instead of 41 s later)
// Published       ws:disconnect {playerId, except, code, reason} when a connection replaces one (the node holding the
//                 old one closes it; the TS websocket left a replaced socket open and stopped pinging it), through the
//                 player's replay log (PlayerMessages.DisconnectAsync)
//
// Unlike the TS websocket: presence (online_players, the heartbeat, the IP's session) goes at the close itself, before
// anything else; TS removed online_players last, after a pre-game dodge's rating, and the match flow's rollback
// PlayerDisconnect calls a player still in online_players a rollback crash (MatchStatusEvents). The heartbeat goes at
// once as it did in TS: the matchmaker drops a ticket whose player has none. Nothing is cleared at startup (another node
// holds its own players).
//
// A node that dies closes nothing: its players' presence stays, and no disconnected event is appended for them. The other
// nodes take them offline as the close would have (GatewayReaper, ReapAsync), with a disconnected event marked reaped, once
// the node is gone from the instance registry and the player has not answered a ping for a while; the readers take a
// reaped close in a match for a crash (the server failed the player), not a leave. TS had no such thing: a websocket that
// crashed left its players' heartbeats, sessions, tickets and lobbies, and its restart cleared only online_players.

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

    // The connection becomes the player's current one; returns the id of the one it replaced ('' for none) and the head of
    // the player's replay log ('' for none), read with the claim: what an edge's connection has already missed.
    private const string ClaimScript = """
        local old = redis.call('HGET', KEYS[1], 'id')
        redis.call('HSET', KEYS[1], 'id', ARGV[1], 'node', ARGV[2], 'ip', ARGV[3], 'at', ARGV[4], 'token', ARGV[6], 'attach', ARGV[7], 'edge', ARGV[8])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        local head = redis.call('XREVRANGE', KEYS[2], '+', '-', 'COUNT', 1)[1]
        return {old or '', head and head[1] or ''}
        """;

    // Only the current connection, on the socket that holds it now, lets the player go: 1 when it did. An entry without
    // attach (an older node's) is let go by its id.
    private const string ReleaseScript = """
        if redis.call('HGET', KEYS[1], 'id') == ARGV[1] then
          local attach = redis.call('HGET', KEYS[1], 'attach')
          if not attach or attach == '' or attach == ARGV[2] then
            redis.call('DEL', KEYS[1])
            return 1
          end
        end
        return 0
        """;

    /// <summary>
    /// A connection completed its handshake: it becomes the player's current one (a connection it replaces is asked to
    /// close, wherever it is), and the player is online. Returns the replaced connection's id, if any, and the head of the
    /// player's replay log at the claim.
    /// </summary>
    public static async Task<GatewayClaim> ConnectedAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        var claim = (RedisResult[])(await redis.ScriptEvaluateAsync(ClaimScript, [ConnectionKey(connection.PlayerId), PlayerMessages.LogKey(connection.PlayerId)],
            [connection.Id, connection.Node, connection.Ip, ms, (long)ConnectionTtl.TotalMilliseconds, connection.TokenHash, connection.Attachment, connection.Edge]))!;
        string? replaced = (string?)claim[0];
        if (replaced == connection.Id || replaced == "")
        {
            replaced = null;
        }

        StreamId.TryParse((string?)claim[1], out var head);

        await redis.SortedSetAddAsync(Heartbeats, connection.PlayerId, ms);
        await redis.SetAddAsync(OnlinePlayers, connection.PlayerId);
        await TouchSessionAsync(redis, connection, ms);
        if (replaced is not null)
        {
            await AppendAsync(redis, "replaced", connection.PlayerId, replaced, ms, [new("replacedBy", connection.Id)]);
            await PlayerMessages.DisconnectAsync(redis, new JsonObject
            {
                ["playerId"] = connection.PlayerId,
                ["except"] = connection.Id,
                ["code"] = 1000,
                ["reason"] = "replaced",
            });
        }

        await AppendAsync(redis, "connected", connection, ms);
        return new GatewayClaim(replaced, head);
    }

    // An edge's connection re-attached on another socket (another node, or this one again): only while it is still the
    // player's current connection, opened with the same session (not replaced by a newer login, not let go of by its old
    // socket or the reaper); 1 when it was. The node and the socket holding it now are written, so only this socket
    // releases it, and the reaper leaves it alone.
    private const string ResumeScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] or redis.call('HGET', KEYS[1], 'token') ~= ARGV[4] then
          return 0
        end
        redis.call('HSET', KEYS[1], 'node', ARGV[2], 'attach', ARGV[3], 'edge', ARGV[6])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return 1
        """;

    /// <summary>
    /// An edge re-attached the player's connection here (<paramref name="connection"/>: its id and session as before,
    /// this node, a new socket): it is theirs again only if it is still their current connection; then the player is
    /// touched as an answer to the ping touches them, and a resumed event is appended (no connected: the game never
    /// left). False: refused.
    /// </summary>
    public static async Task<bool> ResumedAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        if ((long)await redis.ScriptEvaluateAsync(ResumeScript, [ConnectionKey(connection.PlayerId)],
            [connection.Id, connection.Node, connection.Attachment, connection.TokenHash, (long)ConnectionTtl.TotalMilliseconds, connection.Edge]) != 1)
        {
            return false;
        }

        await redis.SortedSetAddAsync(Heartbeats, connection.PlayerId, ms);
        await TouchSessionAsync(redis, connection, ms);
        await AppendAsync(redis, "resumed", connection, ms);
        return true;
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
    /// is pending); true when it was the current one, and a disconnected event was appended. <paramref name="reaped"/>:
    /// no game asked for this close (an edge's link that never came back), so the readers take it as the server's failure,
    /// as a reaped close.
    /// </summary>
    public static async Task<bool> ClosedAsync(IDatabase redis, GatewayConnectionInfo connection, DateTimeOffset now, bool reaped = false)
    {
        if ((long)await redis.ScriptEvaluateAsync(ReleaseScript, [ConnectionKey(connection.PlayerId)], [connection.Id, connection.Attachment]) != 1)
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

        await AppendAsync(redis, "disconnected", connection, now.ToUnixTimeMilliseconds(), reaped);
        return true;
    }

    // A connection of a node that is gone, as its close would have let the player go (ClosedAsync), only while the player's
    // current connection is still that one on that node (''/'' for none at all: its entry ran out) and their last answer
    // is older than ARGV[4]: one of several nodes doing this at once wins, and a connection that came back (another
    // node, the same id: the edge's re-attach) is left alone. 1 when it was reaped.
    private const string ReapScript = """
        local id = redis.call('HGET', KEYS[1], 'id') or ''
        if id ~= ARGV[2] or (id ~= '' and (redis.call('HGET', KEYS[1], 'node') or '') ~= ARGV[3]) then
          return 0
        end
        local seen = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not seen or tonumber(seen) > tonumber(ARGV[4]) then
          return 0
        end
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        if redis.call('EXISTS', KEYS[4]) == 0 then
          redis.call('SREM', KEYS[3], ARGV[1])
          if ARGV[7] ~= '' then
            redis.call('ZREM', KEYS[5], ARGV[1])
          end
        end
        redis.call('XADD', KEYS[6], 'MAXLEN', '~', 10000, '*', 'type', 'disconnected', 'player', ARGV[1], 'connection', ARGV[2],
          'at', ARGV[5], 'node', ARGV[3], 'ip', ARGV[7], 'token', ARGV[6], 'reaped', '1')
        return 1
        """;

    /// <summary>
    /// The player's connection <paramref name="connectionId"/> on <paramref name="node"/>, a node that is gone, closes as
    /// <see cref="ClosedAsync"/> would have closed it (a disconnected event, marked reaped), if it is still their current
    /// one and they have not answered a ping since <paramref name="answeredBeforeMs"/>. An empty id: the player has no
    /// connection entry left at all. True when this call reaped it.
    /// </summary>
    public static async Task<bool> ReapAsync(IDatabase redis, string playerId, string connectionId, string node, string ip, string tokenHash,
        long answeredBeforeMs, DateTimeOffset now, string stream = ConnectionsStream) =>
        (long)await redis.ScriptEvaluateAsync(ReapScript,
            [ConnectionKey(playerId), Heartbeats, OnlinePlayers, $"rejoin_pending:{playerId}", $"active_ip_accounts:{ip}", stream],
            [playerId, connectionId, node, answeredBeforeMs, now.ToUnixTimeMilliseconds(), tokenHash, ip]) == 1;

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
    private static Task AppendAsync(IDatabase redis, string type, GatewayConnectionInfo connection, long ms, bool reaped = false) =>
        AppendAsync(redis, type, connection.PlayerId, connection.Id, ms,
            [new("node", connection.Node), new("ip", connection.Ip), new("token", connection.TokenHash), .. reaped ? new NameValueEntry[] { new("reaped", "1") } : []]);

    private static Task AppendAsync(IDatabase redis, string type, string playerId, string connectionId, long ms, NameValueEntry[] fields) =>
        redis.StreamAddAsync(ConnectionsStream,
            [new("type", type), new("player", playerId), new("connection", connectionId), new("at", ms), .. fields],
            maxLength: 10_000, useApproximateMaxLength: true);
}

/// <summary>
/// One game connection: its id (minted where the socket is held: the node, or the edge in front of it), the player, the
/// node holding it, the client's IP (as the reverse proxy reports it), the hash of the session token it was opened with
/// (<see cref="GatewayPresence.TokenHash"/>), and the socket holding it on that node (<paramref name="Attachment"/>,
/// minted per socket: an edge's connection keeps its id from node to node, and only its current socket releases it), and
/// the edge instance the game comes through (<paramref name="Edge"/>; empty: connected directly).
/// </summary>
public sealed record GatewayConnectionInfo(string Id, string PlayerId, string Node, string Ip, string TokenHash, string Attachment = "", string Edge = "");

/// <summary>What a connection's claim found: the connection it replaced (null: none), and the head of the player's replay log.</summary>
public sealed record GatewayClaim(string? Replaced, StreamId LogHead);

/// <summary>The channels every gateway node hears.</summary>
public static class GatewayChannels
{
    /// <summary>
    /// {playerIds, message, seqs?}: each node sends message to the players whose current connection it holds. seqs
    /// ({player: stream id}) names each player's entry in their replay log (<see cref="PlayerMessages"/>).
    /// </summary>
    public const string Send = ProfileNotifications.WsSendChannel;

    /// <summary>
    /// {playerId, connectionId?, except?, code?, reason?, seq?}: the node holding the player's connection closes it, only if it
    /// is <c>connectionId</c> and is not <c>except</c> when those are given; with a code, a close handshake with that
    /// code and reason, without one at once (the ops command's, as the TS websocket's terminate()).
    /// </summary>
    public const string Disconnect = Ops.OpsService.DisconnectChannel;
}
