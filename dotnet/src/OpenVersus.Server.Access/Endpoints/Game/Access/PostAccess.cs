using FastEndpoints;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Access.Endpoints.Game.Access;

/// <summary>
/// POST /access: the game's login. Seen in: binary 0x144f9f5b0; captured 11x; TS server: POST /access.
/// The body (auth.steam, an encrypted app ticket; metadata; options) is not read: see AccessService.
/// </summary>
[NoHydraToken]
public sealed class PostAccess : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/access");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string ip = ClientAddress.Of(HttpContext, stripMapped: true);
        string? token = HttpContext.Request.Headers["x-hydra-access-token"] is { Count: > 0 } header ? header.ToString() : null;
        switch (await Resolve<IAccessService>().LoginAsync(ip, token, ct))
        {
            case AccessResult.Ok ok:
                await HydraBodies.WriteAsync(HttpContext, ok.Response, ct);
                break;
            case AccessResult.Banned:
                // As the TS server: 200 and nothing else.
                await Send.ResultAsync(Results.Ok());
                break;
            default:
                await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
                break;
        }
    }
}
