using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/me/relationships/{id}/unblock: the token's player unblocks {id}: the TS route removes the friendship and the block
/// (<see cref="IFriendRequests.RemoveAsync"/>). {} whatever came of it (an empty id does nothing), as there.
/// Seen in: binary 0x144ff4890; TS server: PUT /accounts/me/relationships/{blockid}/unblock.
/// </summary>
public sealed class PutAccountsMeRelationshipsByIdUnblock : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/me/relationships/{id}/unblock");
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
