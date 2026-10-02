using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/catalog/{id}/products.
/// Seen in: binary 0x144fdb040.
/// </summary>
public sealed class GetCommerceCatalogByIdProducts : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/catalog/{id}/products");
    }
}
