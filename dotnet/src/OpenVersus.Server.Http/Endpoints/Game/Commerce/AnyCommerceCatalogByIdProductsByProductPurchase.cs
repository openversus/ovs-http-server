using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// Any method /commerce/catalog/{id}/products/{product}/purchase.
/// Seen in: binary 0x144fdbda0.
/// Method not read yet; body has price_slug.
/// </summary>
public sealed class AnyCommerceCatalogByIdProductsByProductPurchase : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/commerce/catalog/{id}/products/{product}/purchase");
    }
}
