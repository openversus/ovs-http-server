using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// PUT /ovs/friends/send-request/{targetId}: a friend request from the OpenVersus client's player search
/// (<see cref="IFriendRequests"/>), the receiver's name from their player document. Answers the result,
/// {success: false, error: "player_not_found"} or, when anything fails (a target id that is not an ObjectId included),
/// {success: false, error: "server_error"}, all with 200; 401 {error: "not_connected"} (after the session token check, as there: 401 without one) without a player, as the TS
/// server did. Seen in: TS server: PUT /ovs/friends/send-request/{targetId}. Server only.
/// </summary>
[HydraTokenRequired]
public sealed class PutOvsFriendsSendRequestByTargetId : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ovs/friends/send-request/{targetId}");
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

            string targetId = Route<string>("targetId") ?? "";
            var friends = Resolve<IFriendRequests>();
            if (await friends.PlayerNameAsync(targetId, ct) is not { } target)
            {
                await SendJsonAsync(new FriendRequestResult(false, "player_not_found").Json(), ct);
                return;
            }

            string myName = PostOvsFriendsRequest.Or(PostOvsFriendsRequest.Field(me, "username"), PostOvsFriendsRequest.Field(me, "hydraUsername"), "Unknown");
            var result = await friends.SendAsync(me.Id, myName, targetId, target.Name ?? "", ct);
            Logger.LogInformation("[FriendReq]: {From} -> {To}: {Result}", PostOvsFriendsRequest.Field(me, "username"), target.Name, result.Success ? "sent" : result.Error);
            await SendJsonAsync(result.Json(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[FriendReq]: Error: {Error}", e.Message);
            await SendJsonAsync(new FriendRequestResult(false, "server_error").Json(), ct);
        }
    }
}
