using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Layout;

/// <summary>
/// GET /layout/{layout_type}/personalized/{variant}/{id}.
/// Seen in: binary 0x144fd9620; captured 96x; TS server: GET /layout/dokken-layout-type/personalized/account-cosmetics-variant/{id}, GET /layout/dokken-layout-type/personalized/battlepass-variant/{id}, GET /layout/dokken-layout-type/personalized/currency-variant/{id} ....
/// </summary>
public sealed class GetLayoutByLayoutTypePersonalizedByVariantById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/layout/{layout_type}/personalized/{variant}/{id}");
    }
}
