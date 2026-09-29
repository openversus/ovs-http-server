using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/steam/mtx_user_info/{id}.
/// Seen in: binary 0x144fd98d0; captured 11x; TS server: GET /commerce/steam/mtx_user_info/me.
/// Id is 'me' in captures.
/// </summary>
public sealed class GetCommerceSteamMtxUserInfoById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/steam/mtx_user_info/{id}");
    }
}
