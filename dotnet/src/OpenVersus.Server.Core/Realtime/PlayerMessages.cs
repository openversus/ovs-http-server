using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

/// <summary>
/// What a player's game is told outside an answer: websocket messages (through ws:send, which every websocket node
/// hears and delivers to the players it holds; docs/MIGRATION-BRIDGES.md 4 while that is the TS websocket), and the
/// OpenVersus client's notification queue (dll_notifications:{player}, polled through GET /ovs/notifications).
/// </summary>
public static class PlayerMessages
{
    /// <summary>Sends <paramref name="message"/>, as it is, to each of <paramref name="playerIds"/> that is connected.</summary>
    public static Task SendAsync(IDatabase redis, IEnumerable<string> playerIds, JsonObject message)
    {
        var ids = new JsonArray(playerIds.Select(id => (JsonNode?)id).ToArray());
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return redis.PublishAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), Js.Stringify(new JsonObject { ["playerIds"] = ids, ["message"] = message }));
    }

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
