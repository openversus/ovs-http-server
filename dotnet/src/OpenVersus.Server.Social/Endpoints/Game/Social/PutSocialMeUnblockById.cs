using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Social;

/// <summary>
/// PUT /social/me/unblock/{id}: the token's player unblocks {id}: the TS route removes the friendship and the block
/// (<see cref="IFriendRequests.RemoveAsync"/>). {} whatever came of it (an empty id does nothing), as there.
/// Seen in: binary 0x140fa3540; TS server: PUT /social/me/unblock/{blockid}.
/// </summary>
public sealed class PutSocialMeUnblockById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/social/me/unblock/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string me = HttpContext.Session()?.AccountId ?? "", target = Route<string>("id") ?? "";
        if (target.Length > 0)
        {
            try
            {
                await Resolve<IFriendRequests>().RemoveAsync(me, target, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Logger.LogError("PUT unblock of {Target}: {Error}", target, e.Message);
            }
        }

        await SendJsonAsync(new JsonObject(), ct);
    }
}
