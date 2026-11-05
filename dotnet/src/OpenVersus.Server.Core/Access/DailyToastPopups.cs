using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Access;

// The daily toast bonus's popup (OnRewardsGranted), shown when the game connects. /access grants the bonus and arms
// daily_toast_bonus_pending:{player} with the count (AccessService, 5 minutes); the realtime gateway's connected event
// (realtime:connections, read by the access service as the consumer group "access": ConnectionEvents.cs) sends the popup,
// as the TS websocket did at its handshake (drainDailyToastBonusPending). Only for the player's current connection: a
// connection already closed or replaced leaves the flag to the one that took over.
//
// Redis, read     realtime:conn:{player} (id)
// Redis, taken    daily_toast_bonus_pending:{player} (GETDEL)
// Sent (ws:send)  OnRewardsGranted {Context: "Default", RewardsGranted: [{RewardGuid: "OVS-DAILY-TOAST-BONUS", Constraints: [],
//                 RewardGrantMethod: "DirectInventoryItem", InventoryHsda: "match_toasts", DirectInventoryItemCount}]},
//                 profile-notification, as TS sent it
//
// Unlike the TS websocket: the flag is taken before the popup goes (TS sent it, then deleted the flag: a game that
// connected again in between was shown it twice). A count that is not a whole number above 0 is dropped, as there (TS
// read it with parseInt: only AccessService writes it, always a plain number).

/// <summary>The access service's reader of the realtime gateway's connections (see the header of DailyToastPopups.cs).</summary>
internal sealed class DailyToastPopups(IServiceProvider services, TimeProvider time, ILogger<DailyToastPopups> logger)
    : ConnectionEventsReader(services, time, logger)
{
    public const string GroupName = "access";

    protected override string Group => GroupName;

    protected override async Task OnEventAsync(IDatabase redis, ConnectionEvent connectionEvent)
    {
        string player = connectionEvent.PlayerId;
        if (connectionEvent.Type != "connected"
            || (string?)await redis.HashGetAsync(GatewayPresence.ConnectionKey(player), "id") != connectionEvent.ConnectionId)
        {
            return;
        }

        if ((string?)await redis.StringGetDeleteAsync($"daily_toast_bonus_pending:{player}") is not { } raw)
        {
            return;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0)
        {
            Log.LogWarning("Daily toast bonus popup: invalid amount \"{Raw}\" for {Player}, dropped", raw, player);
            return;
        }

        await PlayerMessages.SendAsync(redis, [player], Popup(player, count));
        Log.LogInformation("Sent daily toast bonus popup (+{Count}) to {Player}", count, player);
    }

    internal static JsonObject Popup(string player, int count) => new()
    {
        ["data"] = new JsonObject
        {
            ["template_id"] = "OnRewardsGranted",
            ["Context"] = "Default",
            ["RewardsGranted"] = new JsonArray(new JsonObject
            {
                ["RewardGuid"] = "OVS-DAILY-TOAST-BONUS",
                ["Constraints"] = new JsonArray(),
                ["RewardGrantMethod"] = "DirectInventoryItem",
                ["InventoryHsda"] = "match_toasts",
                ["DirectInventoryItemCount"] = count,
            }),
        },
        ["payload"] = new JsonObject
        {
            ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
            ["template"] = "realtime",
            ["account_id"] = player,
            ["profile_id"] = player,
        },
        ["header"] = "",
        ["cmd"] = "profile-notification",
    };
}

public static class DailyToastPopupsHosting
{
    /// <summary>The access service's reader of the realtime gateway's connections: the daily toast bonus's popup.</summary>
    public static WebApplicationBuilder AddDailyToastPopups(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<DailyToastPopups>();
        return builder;
    }
}
