using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Store;

/// <summary>
/// GET /store/store_products/{id}/my_products.
/// Seen in: binary 0x144feb090.
/// Segment order inferred.
/// </summary>
public sealed class GetStoreStoreProductsByIdMyProducts : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/store/store_products/{id}/my_products");
    }
}
