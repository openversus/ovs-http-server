using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/send_profile_notification {TargetAccountId, Type}: a type naming "unfriend" or "remove" removes the
/// friend, else one naming "friend" or "request" sends a friend request (<see cref="IFriendRequests"/>), else nothing.
/// Answers {body: {}, metadata: null, return_code: 200} whatever happened, as the TS server did.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/send_profile_notification. Ssc: binary.
/// </summary>
public sealed class PutSendProfileNotification : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/send_profile_notification");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            string myId = HttpContext.Session()?.AccountId ?? "";
            var body = await ReadBodyAsync(ct) as JsonObject ?? [];
            // The first truthy of each spelling, as JavaScript's || takes it; a target that is not text is one too.
            string? target = First(body, "TargetAccountId", "targetAccountId", "target_account_id", "AccountId", "accountId");
            if (target is null)
            {
                Logger.LogWarning("send_profile_notification missing TargetAccountId");
            }
            else if (First(body, "Type", "type", "template_id") is { } type)
            {
                var redis = Resolve<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
                var mine = await redis.HashGetAsync($"connections:{myId}", ["username", "hydraUsername"]);
                var theirs = await redis.HashGetAsync($"connections:{target}", ["username", "hydraUsername"]);
                string lower = type.ToLowerInvariant();
                var friends = Resolve<IFriendRequests>();
                // "unfriend" before "friend": it contains it.
                if (lower.Contains("unfriend") || lower.Contains("remove"))
                {
                    await friends.RemoveAsync(myId, target, ct);
                }
                else if (lower.Contains("friend") || lower.Contains("request"))
                {
                    var result = await friends.SendAsync(myId, Or(mine), target, Or(theirs), ct);
                    Logger.LogInformation("Friend request result: {Result}", result.Json().ToJsonString());
                }
                else
                {
                    Logger.LogInformation("Unknown profile notification type \"{Type}\", ignoring", type);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("Error in send_profile_notification: {Error}", e.Message);
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 200 }, ct);
    }

    // a || b || ...: the first truthy value, as text; null when none is (or the first truthy one is not text, which
    // the TS code could not have used either: its string calls threw, and its catch answered the same).
    private static string? First(JsonObject body, params string[] keys)
    {
        foreach (string key in keys)
        {
            var value = body[key];
            if (value is JsonValue v && v.TryGetValue(out string? s))
            {
                if (s.Length > 0)
                {
                    return s;
                }

                continue;
            }

            if (value is null || (value is JsonValue b && ((b.TryGetValue(out bool flag) && !flag) || (b.TryGetValue(out double d) && (d == 0 || double.IsNaN(d))))))
            {
                continue;
            }

            return null;
        }

        return null;
    }

    private static string Or(StackExchange.Redis.RedisValue[] names) =>
        names.Select(n => n.HasValue ? n.ToString() : null).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "Unknown";
}
