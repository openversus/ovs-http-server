using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/steam/mtx_user_info/{id}: for "me", a fixed US Steam wallet (Static/commerce-steam-mtx-user-info-me.json),
/// as the TS server answers; it has no route for any other id. Seen in: binary 0x144fd98d0; captured 11x; TS server:
/// GET /commerce/steam/mtx_user_info/me.
/// </summary>
public sealed class GetCommerceSteamMtxUserInfoById : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/steam/mtx_user_info/{id}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("id") == "me" ? SendStaticAsync("commerce-steam-mtx-user-info-me", ct) : SendTsCatchAllAsync(ct);
}
