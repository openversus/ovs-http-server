using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/invitations/{id}/accept: the token's player accepts the friend request (<see cref="IFriendRequests.AcceptAsync"/>).
/// {status: "ok"} whatever came of it, as the TS route answers (the result only logged).
/// Seen in: binary 0x140f932a0; TS server: PUT /friends/me/invitations/{id}/accept.
/// </summary>
public sealed class PutFriendsMeInvitationsByIdAccept : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/invitations/{id}/accept");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string me = HttpContext.Session()?.AccountId ?? "", id = Route<string>("id") ?? "";
        try
        {
            var result = await Resolve<IFriendRequests>().AcceptAsync(id, me, ct);
            Logger.LogInformation("[Friends]: Accept invitation {Request} by {Account}: {Result}", id, me, result.Json().ToJsonString());
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("[Friends]: Error accepting invitation: {Error}", e.Message);
        }

        await SendJsonAsync(new JsonObject { ["status"] = "ok" }, ct);
    }
}
