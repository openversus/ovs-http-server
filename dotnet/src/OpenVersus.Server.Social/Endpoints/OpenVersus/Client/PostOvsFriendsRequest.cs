using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/request {targetId}: a friend request from the OpenVersus client (<see cref="IFriendRequests"/>), the
/// sender found as the TS resolveAccountFromRequest finds them, the receiver's name from their session. Answers the
/// result (200), 401 {error: "not_connected"} (after the session token check, as there: 401 without one), 400 {error: "targetId required"} or 500 {error: "internal_error"}, as the
/// TS server did. Seen in: TS server: POST /ovs/friends/request. Server only.
/// </summary>
[HydraTokenRequired]
public sealed class PostOvsFriendsRequest : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/request");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            if (await Resolve<IAccountResolver>().ResolveAsync(AccountLookups.From(HttpContext)) is not { Id.Length: > 0 } me)
            {
                await SendJsonAsync(new JsonObject { ["error"] = "not_connected" }, 401, ct);
                return;
            }

            // const { targetId } = req.body; if (!targetId): any value but a non-empty string is refused here.
            if ((await ReadBodyAsync(ct))?["targetId"] is not JsonValue v || !v.TryGetValue(out string? targetId) || targetId.Length == 0)
            {
                await SendJsonAsync(new JsonObject { ["error"] = "targetId required" }, 400, ct);
                return;
            }

            var redis = Resolve<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
            var target = await redis.HashGetAsync($"connections:{targetId}", ["username", "hydraUsername"]);
            string targetName = Or(target[0], target[1], "Unknown");
            string myName = Or(Field(me, "username"), Field(me, "hydraUsername"), "Unknown");
            var result = await Resolve<IFriendRequests>().SendAsync(me.Id, myName, targetId, targetName, ct);
            await SendJsonAsync(result.Json(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: Error sending friend request: {Error}", e.Message);
            await SendJsonAsync(new JsonObject { ["error"] = "internal_error" }, 500, ct);
        }
    }

    internal static string? Field(ResolvedAccount account, string name) =>
        account.Connection.FirstOrDefault(e => e.Name == name).Value is { HasValue: true } v ? v.ToString() : null;

    internal static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static string Or(StackExchange.Redis.RedisValue a, StackExchange.Redis.RedisValue b, string fallback) =>
        Or(a.HasValue ? a.ToString() : null, b.HasValue ? b.ToString() : null, fallback);
}
