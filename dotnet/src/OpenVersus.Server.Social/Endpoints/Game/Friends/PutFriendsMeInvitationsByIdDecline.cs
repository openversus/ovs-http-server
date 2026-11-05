using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/invitations/{id}/decline: the token's player declines the friend request (<see cref="IFriendRequests.DeclineAsync"/>).
/// {status: "ok"} whatever came of it, as the TS route answers.
/// Seen in: binary 0x140f97d00; TS server: PUT /friends/me/invitations/{id}/decline.
/// </summary>
public sealed class PutFriendsMeInvitationsByIdDecline : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/invitations/{id}/decline");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string me = HttpContext.Session()?.AccountId ?? "", id = Route<string>("id") ?? "";
        try
        {
            var result = await Resolve<IFriendRequests>().DeclineAsync(id, me, ct);
            Logger.LogInformation("[Friends]: Decline invitation {Request} by {Account}: {Result}", id, me, result.Json().ToJsonString());
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: Error declining invitation: {Error}", e.Message);
        }

        await SendJsonAsync(new JsonObject { ["status"] = "ok" }, ct);
    }
}
