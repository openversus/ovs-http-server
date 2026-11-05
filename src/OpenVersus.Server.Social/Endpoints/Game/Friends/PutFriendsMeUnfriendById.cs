using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/unfriend/{publicId}: the game removes a friend, named by public id (looked up to the account,
/// <see cref="IFriendRequests.IdByPublicIdAsync"/>; none: only logged), both out of each other's lists
/// (<see cref="IFriendRequests.RemoveAsync"/>). {status: "ok"} whatever came of it, as the TS route answers.
/// Seen in: binary 0x140fa3780; TS server: PUT /friends/me/unfriend/{id}.
/// </summary>
public sealed class PutFriendsMeUnfriendById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/unfriend/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string me = HttpContext.Session()?.AccountId ?? "", publicId = Route<string>("id") ?? "";
        try
        {
            var requests = Resolve<IFriendRequests>();
            if (await requests.IdByPublicIdAsync(publicId, ct) is { } friendId)
            {
                await requests.RemoveAsync(me, friendId, ct);
                Logger.LogInformation("[Friends]: Unfriend by {Account} of {Friend} (public id {PublicId})", me, friendId, publicId);
            }
            else
            {
                Logger.LogWarning("[Friends]: Could not find player with public_id {PublicId}", publicId);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: Error unfriending: {Error}", e.Message);
        }

        await SendJsonAsync(new JsonObject { ["status"] = "ok" }, ct);
    }
}
