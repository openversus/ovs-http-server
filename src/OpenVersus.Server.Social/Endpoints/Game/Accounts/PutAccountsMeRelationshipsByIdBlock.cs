using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/me/relationships/{id}/block: the token's player blocks {id} (<see cref="IFriendRequests.BlockAsync"/>, the target's name looked up, "Unknown"
/// without one). {} whatever came of it (an empty id does nothing), as the TS route answers.
/// Seen in: binary 0x144fe81e0; TS server: PUT /accounts/me/relationships/{blockid}/block.
/// </summary>
public sealed class PutAccountsMeRelationshipsByIdBlock : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/me/relationships/{id}/block");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string me = HttpContext.Session()?.AccountId ?? "", target = Route<string>("id") ?? "";
        if (target.Length > 0)
        {
            try
            {
                var requests = Resolve<IFriendRequests>();
                string name = (await requests.PlayerNameAsync(target, ct))?.Name is { Length: > 0 } found ? found : "Unknown";
                var result = await requests.BlockAsync(me, target, name, ct);
                if (!result.Success)
                {
                    Logger.LogError("Failed to block player {Target} for account {Account}: {Error}", target, me, result.Error);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Logger.LogError("PUT block of {Target}: {Error}", target, e.Message);
            }
        }

        await SendJsonAsync(new JsonObject(), ct);
    }
}
