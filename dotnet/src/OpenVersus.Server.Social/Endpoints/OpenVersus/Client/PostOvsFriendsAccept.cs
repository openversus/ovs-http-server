using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/accept {requestId}: the OpenVersus client accepting a friend request (<see cref="IFriendRequests.AcceptAsync"/>); the result.
/// Answers 401 {error: "not_connected"}, 400 for a missing field, 500 {error: "internal_error"}, as the TS route did.
/// Seen in: TS server: POST /ovs/friends/accept.
/// </summary>
[HydraTokenRequired]
public sealed class PostOvsFriendsAccept : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/accept");
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

            if (Text((await ReadBodyAsync(ct))?["requestId"]) is not { Length: > 0 } requestId)
            {
                await SendJsonAsync(new JsonObject { ["error"] = "requestId required" }, 400, ct);
                return;
            }

            await SendJsonAsync((await Resolve<IFriendRequests>().AcceptAsync(requestId, me.Id, ct)).Json(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: PostOvsFriendsAccept failed: {Error}", e.Message);
            await SendJsonAsync(new JsonObject { ["error"] = "internal_error" }, 500, ct);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
