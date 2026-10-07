using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

/// <summary>
/// Profile notifications (MissionUpdatesComplete, RewardTrackStatesUpdated, ...) as WB's websocket sent them and the TS
/// websocket sent EndOfMatchPayload, delivered through ws:send ({playerIds, message}: the realtime gateway sends message,
/// as it is, to each connected player named).
/// </summary>
public static class ProfileNotifications
{
    public const string WsSendChannel = "ws:send";

    /// <summary>The message around <paramref name="data"/> ({template_id, ...}).</summary>
    public static JsonObject Message(JsonObject data, string playerId) => new()
    {
        ["data"] = data,
        ["payload"] = new JsonObject
        {
            ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
            ["template"] = "realtime",
            ["account_id"] = playerId,
            ["profile_id"] = playerId,
        },
        ["header"] = "",
        ["cmd"] = "profile-notification",
    };

    /// <summary>Sends <paramref name="data"/> to the player.</summary>
    public static Task SendAsync(IDatabase redis, string playerId, JsonObject data) =>
        redis.PublishAsync(RedisChannel.Literal(WsSendChannel), Js.Stringify(new JsonObject
        {
            ["playerIds"] = new JsonArray(playerId),
            ["message"] = Message(data, playerId),
        }));

    /// <summary>RewardTrackStatesUpdated {RewardTrackStates, UpdateContext}, as the client's notification router reads it
    /// (0x140d06c10, build f97148ff). UpdateContext is EMvsRewardTrackUpdateContext: Unknown 0, RewardTrackClaim 1,
    /// MissionClaim 2, EndOfGameProcessing 3, DailyLogin 4, DebugEndpoint 5, XpReward 6.</summary>
    public static JsonObject RewardTrackStatesUpdated(IEnumerable<JsonObject> states, int updateContext) => new()
    {
        ["template_id"] = "RewardTrackStatesUpdated",
        ["RewardTrackStates"] = new JsonArray(states.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
        ["UpdateContext"] = updateContext,
    };
}
