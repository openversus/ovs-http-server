using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// Each player's replay log: what their game was sent, kept for ReplayWindow so that an edge re-attaching the game to
// another gateway node (slice 3f) can replay what it missed while no node held it. Every message and every forced close
// for a player is appended to their log and published in one script, so the log and the publish happen together and in
// the same order for every player; the entry's stream id is the message's sequence (seqs in ws:send, seq in
// ws:disconnect), which the gateway nodes ignore until they serve an edge. Every named player is logged, connected or
// not (the TTL takes care of the others). Not logged: what a node makes itself (the id frame, the ping, the matchmaking
// tick).
//
// Redis, written  realtime:out:{player} (stream: field message, a ws:send message as it was published, or disconnect, a
//                 ws:disconnect request; XADD MINID drops what is older than ReplayWindow by the Redis clock, exactly;
//                 PEXPIRE ReplayTtl)
// Published       ws:send {playerIds, message, seqs: {player: stream id}}; ws:disconnect {..., seq: stream id}
//                 (the message or request as it would be published without the log, the sequence appended)

/// <summary>
/// What a player's game is told outside an answer: websocket messages (through ws:send, which every websocket node
/// hears and delivers to the players it holds), forced closes (ws:disconnect), and the
/// OpenVersus client's notification queue (dll_notifications:{player}, polled through GET /ovs/notifications).
/// </summary>
public static class PlayerMessages
{
    public static string LogKey(string playerId) => $"realtime:out:{playerId}";

    /// <summary>
    /// How far back a player's log reaches. It must be longer than any gap a resume can cover (a gateway node's death to
    /// the game's re-attach: the edge gives up sooner, and the reaper lets a player of a dead node go once both
    /// Gateway:ReapAfterMs has passed since their last answer and the registry calls the node gone, about 30 s after
    /// the death): then whatever a trim drops was already delivered, and a resume replays everything after the last
    /// message the game received.
    /// </summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a log outlives its newest entry (at least the window, so nothing inside it expires with the key).</summary>
    public static readonly TimeSpan ReplayTtl = TimeSpan.FromMinutes(5);

    // ARGV: the channel, the payload as published without the log, the entry's field and value, the window and the TTL
    // (ms), then the player of each key. The payload goes out with seqs ({player: id}) appended for a message, seq (the
    // one id) for a close. The threshold is written with string.format: a Lua number concatenated is written with 14
    // significant digits (exact for a time in ms, 13 digits, but not for every number).
    private const string LogScript = """
        local now = redis.call('TIME')
        local minid = string.format('%.0f', tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000) - tonumber(ARGV[5]))
        local seqs, seq = {}, nil
        for i, key in ipairs(KEYS) do
          seq = redis.call('XADD', key, 'MINID', minid, '*', ARGV[3], ARGV[4])
          redis.call('PEXPIRE', key, ARGV[6])
          seqs[ARGV[6 + i]] = seq
        end
        local tail = ARGV[3] == 'message' and ',"seqs":' .. cjson.encode(seqs) or ',"seq":' .. cjson.encode(seq)
        return redis.call('PUBLISH', ARGV[1], string.sub(ARGV[2], 1, -2) .. tail .. '}')
        """;

    /// <summary>Sends <paramref name="message"/>, as it is, to each of <paramref name="playerIds"/> that is connected, and appends it to each one's log.</summary>
    public static Task SendAsync(IDatabase redis, IEnumerable<string> playerIds, JsonObject message)
    {
        var ids = playerIds.ToList();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        string json = Js.Stringify(message);
        string payload = $"{{\"playerIds\":{Js.Stringify(new JsonArray([.. ids.Select(id => (JsonNode?)id)]))},\"message\":{json}}}";
        var logged = ids.Where(id => id.Length > 0).Distinct().ToList();
        if (logged.Count == 0)
        {
            return redis.PublishAsync(RedisChannel.Literal(GatewayChannels.Send), payload);
        }

        return LogAsync(redis, GatewayChannels.Send, payload, "message", json, logged);
    }

    /// <summary>
    /// Asks the node holding the player's connection to close it (ws:disconnect: {playerId, connectionId?, except?, code?,
    /// reason?}, <see cref="GatewayChannels.Disconnect"/>), and appends the request to the player's log. The number of
    /// nodes that heard it.
    /// </summary>
    public static async Task<long> DisconnectAsync(IDatabase redis, JsonObject request)
    {
        string json = Js.Stringify(request);
        if (request["playerId"] is not { } player || player.GetValueKind() != System.Text.Json.JsonValueKind.String || ((string)player!).Length == 0)
        {
            return await redis.PublishAsync(RedisChannel.Literal(GatewayChannels.Disconnect), json);
        }

        return (long)await LogAsync(redis, GatewayChannels.Disconnect, json, "disconnect", json, [(string)player!]);
    }

    private static Task<RedisResult> LogAsync(IDatabase redis, string channel, string payload, string field, string entry, List<string> playerIds) =>
        redis.ScriptEvaluateAsync(LogScript, [.. playerIds.Select(id => (RedisKey)LogKey(id))],
            [channel, payload, field, entry, (long)ReplayWindow.TotalMilliseconds, (long)ReplayTtl.TotalMilliseconds, .. playerIds.Select(id => (RedisValue)id)]);

    /// <summary>A lobby update: {data, payload: {custom_notification: "realtime"} (after <paramref name="payloadFirst"/>), header: "", cmd: "update"}.</summary>
    public static JsonObject Update(JsonObject data, JsonObject? payloadFirst = null)
    {
        var payload = payloadFirst ?? new JsonObject();
        payload["custom_notification"] = "realtime";
        return new JsonObject { ["data"] = data, ["payload"] = payload, ["header"] = "", ["cmd"] = "update" };
    }

    public const string NotificationPrefix = "dll_notifications:";

    /// <summary>How long a queued notification waits for the client to poll it.</summary>
    public static readonly TimeSpan NotificationTtl = TimeSpan.FromSeconds(60);

    /// <summary>Queues a notification for the OpenVersus client, as the TS server's redisPushDLLNotification does.</summary>
    public static async Task NotifyClientAsync(IDatabase redis, string playerId, string type, string title, string message, JsonObject data, long timestampMs)
    {
        string key = NotificationPrefix + playerId;
        await redis.ListRightPushAsync(key, Js.Stringify(new JsonObject
        {
            ["type"] = type,
            ["title"] = title,
            ["message"] = message,
            ["data"] = data,
            ["timestamp"] = timestampMs,
        }));
        await redis.KeyExpireAsync(key, NotificationTtl);
    }
}
