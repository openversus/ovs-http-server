using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// PUT /ssc/invoke/toast_player {ContainerMatchId, ToasteeId}, ported from the TS server's handleSsc_invoke_toast_player
// (handlers/ssc.ts), and the toastee's side from the TS websocket's toast:received handler (handleToastReceived): after a
// match, a player toasts another. The toaster pays one match_toasts (adjustMatchToasts(-1), data/playerCounters.ts: never
// below 0; the toast is sent even when they have none), the toastee is granted 2 whether or not their game is
// connected, and only once that grant worked are they shown the toast (ToastReceivedNotification: a popup for a grant
// that failed would not match their inventory). Answers {body: {}, metadata: null, return_code: 0}, whatever happened.
//
// Mongo, written  playercounters {accountId}: created with the defaults when missing (DailyToastBonus.GetCountersAsync,
//                 as getCounters), then for the toaster {$inc: {match_toasts: -1}} where match_toasts >= 1, for the
//                 toastee {$inc: {match_toasts: 2}} (both with updatedAt, as mongoose sets it on every update)
// Sent (ws:send)  ToastReceivedNotification {ToasterAccountID, RewardsGranted: the 2 match_toasts} to the toastee
//
// Unlike there: a ContainerMatchId or ToasteeId that is not text counts as missing (logged, nothing done, as a missing
// one is there).

public interface IMatchToasts
{
    /// <summary>toast_player from <paramref name="accountId"/> (<paramref name="username"/>), the request's body <paramref name="body"/>.</summary>
    Task ToastAsync(string accountId, string username, JsonObject body, CancellationToken ct);
}

internal sealed class MatchToasts(IServiceProvider services, TimeProvider time, ILogger<MatchToasts> log) : IMatchToasts
{
    // TOAST_RECEIVED_REWARD.
    private const int Reward = 2;

    public async Task ToastAsync(string accountId, string username, JsonObject body, CancellationToken ct)
    {
        string? matchId = Text(body["ContainerMatchId"]);
        string? toastee = Text(body["ToasteeId"]);
        if (accountId.Length == 0 || matchId is null || toastee is null)
        {
            log.LogWarning("Toast request missing required fields — account: {Account}, ContainerMatchId: {Match}, ToasteeId: {Toastee}",
                accountId.Length > 0, body["ContainerMatchId"]?.ToJsonString(), body["ToasteeId"]?.ToJsonString());
            return;
        }

        try
        {
            var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            long left = await SpendAsync(mongo, accountId, ct);
            log.LogInformation("Toaster {Account} match_toasts after spend: {Count}", accountId, left);
        }
        catch (Exception e) when (e is MongoException or TimeoutException or InvalidOperationException)
        {
            log.LogError("Failed to decrement match_toasts for toaster {Account}: {Error}", accountId, e.Message);
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Toast from {Account} to {Toastee} not sent: this service has no Redis (REDIS)", accountId, toastee);
            return;
        }

        try
        {
            var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            long count = await GrantAsync(mongo, toastee, ct);
            log.LogInformation("Granted +{Reward} match_toasts to {Toastee}; new count: {Count}", Reward, toastee, count);
        }
        catch (Exception e) when (e is MongoException or TimeoutException or InvalidOperationException)
        {
            log.LogError("Failed to grant +{Reward} match_toasts to {Toastee}, no toast shown: {Error}", Reward, toastee, e.Message);
            return;
        }

        await ProfileNotifications.SendAsync(redis, toastee, new JsonObject
        {
            ["template_id"] = "ToastReceivedNotification",
            ["ToasterAccountID"] = accountId,
            ["RewardsGranted"] = new JsonArray(new JsonObject
            {
                ["RewardGuid"] = "OVS-TOAST-TOAST-0001",
                ["Constraints"] = new JsonArray(),
                ["RewardGrantMethod"] = "DirectInventoryItem",
                ["InventoryHsda"] = "match_toasts",
                ["DirectInventoryItemCount"] = Reward,
            }),
        });
        log.LogInformation("Toast: {Username} ({Account}) toasted {Toastee} in match {Match}", username, accountId, toastee, matchId);
    }

    // adjustMatchToasts(accountId, +2): the document first (with its defaults), then the increment; the count after.
    private async Task<long> GrantAsync(IMongoDatabase mongo, string accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await DailyToastBonus.GetCountersAsync(mongo, accountId, now, ct);
        var granted = await mongo.GetCollection<BsonDocument>(DailyToastBonus.Collection).FindOneAndUpdateAsync(
            new BsonDocument("accountId", accountId),
            new BsonDocument
            {
                { "$set", new BsonDocument("updatedAt", now.UtcDateTime) },
                { "$inc", new BsonDocument("match_toasts", Reward) },
            },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        return granted?["match_toasts"].ToInt64() ?? throw new InvalidOperationException($"no playercounters document for {accountId} after creating it");
    }

    // adjustMatchToasts(accountId, -1): the count after, unchanged when there was none to spend.
    private async Task<long> SpendAsync(IMongoDatabase mongo, string accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await DailyToastBonus.GetCountersAsync(mongo, accountId, now, ct);
        var spent = await mongo.GetCollection<BsonDocument>(DailyToastBonus.Collection).FindOneAndUpdateAsync(
            new BsonDocument { { "accountId", accountId }, { "match_toasts", new BsonDocument("$gte", 1) } },
            new BsonDocument
            {
                { "$set", new BsonDocument("updatedAt", now.UtcDateTime) },
                { "$inc", new BsonDocument("match_toasts", -1) },
            },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        if (spent is not null)
        {
            return spent["match_toasts"].ToInt64();
        }

        var current = await DailyToastBonus.GetCountersAsync(mongo, accountId, now, ct);
        log.LogWarning("match_toasts decrement (-1) skipped for {Account}; balance {Count} insufficient", accountId, current["match_toasts"].ToInt64());
        return current["match_toasts"].ToInt64();
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;
}

public static class MatchToastsHosting
{
    public static WebApplicationBuilder AddMatchToasts(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMatchToasts, MatchToasts>();
        return builder;
    }
}
