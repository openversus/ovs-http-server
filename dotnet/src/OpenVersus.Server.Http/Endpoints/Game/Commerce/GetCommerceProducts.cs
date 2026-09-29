using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/products.
/// Seen in: captured 22x; TS server: GET /commerce/products.
/// Capture only; server only.
/// </summary>
public sealed class GetCommerceProducts : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/products");
    }
}
