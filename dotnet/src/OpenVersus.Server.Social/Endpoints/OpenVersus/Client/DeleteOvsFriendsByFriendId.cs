using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// DELETE /ovs/friends/{friendId}: the OpenVersus client removing a friend (<see cref="IFriendRequests.RemoveAsync"/>); {success: true}.
/// Answers 401 {error: "not_connected"}, 400 for a missing field, 500 {error: "internal_error"}, as the TS route did.
/// Seen in: TS server: DELETE /ovs/friends/{friendId}.
/// </summary>
[HydraTokenRequired]
public sealed class DeleteOvsFriendsByFriendId : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.DELETE);
        Routes("/ovs/friends/{friendId}");
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

            await Resolve<IFriendRequests>().RemoveAsync(me.Id, Route<string>("friendId") ?? "", ct);
            await SendJsonAsync(new FriendRequestResult(true).Json(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: DeleteOvsFriendsByFriendId failed: {Error}", e.Message);
            await SendJsonAsync(new JsonObject { ["error"] = "internal_error" }, 500, ct);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
