using FastEndpoints;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Access;

/// <summary>
/// DELETE /access: the game logging out. Seen in: binary 0x144fab980; captured 9x; TS server: DELETE /access.
/// The TS server only logs it and answers 200 with no body.
/// </summary>
[NoHydraToken]
public sealed class DeleteAccess : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.DELETE);
        Routes("/access");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Resolve<IAccessService>().LogoutAsync(ClientAddress.Of(HttpContext, stripMapped: true), ct);
        await Send.ResultAsync(Results.Ok());
    }
}
