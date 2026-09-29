using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/purchases/{id}.
/// Seen in: binary 0x144fdb4d0; captured 22x; TS server: GET /commerce/purchases/me.
/// Id is 'me' in captures.
/// </summary>
public sealed class GetCommercePurchasesById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/purchases/{id}");
    }
}
