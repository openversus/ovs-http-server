using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// POST /friends/me/invitations {account_id}: a friend request from the game's own UI (<see cref="IFriendRequests"/>);
/// the invitation, or 400 {error: "Failed to send friend request"} when there is no such receiver or an id is not an
/// ObjectId, as the TS server answered. Seen in: binary 0x140f9b5e0; TS server: POST /friends/me/invitations.
/// </summary>
public sealed class PostFriendsMeInvitations : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/friends/me/invitations");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = HttpContext.Session();
        string senderId = session?.AccountId ?? "";
        string? senderUsername = session?.Claims["username"] is JsonValue u && u.TryGetValue(out string? name) ? name : null;
        try
        {
            string receiverId = (await ReadBodyAsync(ct))?["account_id"] is JsonValue v && v.TryGetValue(out string? id) ? id : "";
            Logger.LogInformation("Friend request from {Sender} to {Receiver}", senderId, receiverId);
            await SendJsonAsync(await Resolve<IFriendRequests>().InviteAsync(senderId, senderUsername, receiverId, ct), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("send invitation error: {Error}", e.Message);
            await SendJsonAsync(new JsonObject { ["error"] = "Failed to send friend request" }, 400, ct);
        }
    }
}
