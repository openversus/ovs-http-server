using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/block {targetId, targetUsername}: the OpenVersus client blocking a player (<see cref="IFriendRequests.BlockAsync"/>,
/// "Unknown" without a name); the result.
/// Answers 401 {error: "not_connected"}, 400 for a missing field, 500 {error: "internal_error"}, as the TS route did.
/// Seen in: TS server: POST /ovs/friends/block.
/// </summary>
[HydraTokenRequired]
public sealed class PostOvsFriendsBlock : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/block");
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

            var body = await ReadBodyAsync(ct);
            if (Text(body?["targetId"]) is not { Length: > 0 } targetId)
            {
                await SendJsonAsync(new JsonObject { ["error"] = "targetId required" }, 400, ct);
                return;
            }

            string targetName = Text(body?["targetUsername"]) is { Length: > 0 } given ? given : "Unknown";
            await SendJsonAsync((await Resolve<IFriendRequests>().BlockAsync(me.Id, targetId, targetName, ct)).Json(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: PostOvsFriendsBlock failed: {Error}", e.Message);
            await SendJsonAsync(new JsonObject { ["error"] = "internal_error" }, 500, ct);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
