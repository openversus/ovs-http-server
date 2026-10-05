using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/products: the store's product list, fixed (Static/commerce-products*.json). With partial_response the
/// TS server answers a second list. Seen in: captured 22x; TS server: GET /commerce/products.
/// </summary>
public sealed class GetCommerceProducts : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/products");
    }

    // req.query.partial_response is truthy: present and not empty (a repeated one is an array, always truthy).
    public override Task HandleAsync(CancellationToken ct) =>
        SendStaticAsync(HttpContext.Request.Query["partial_response"] is { Count: > 1 } or [{ Length: > 0 }] ? "commerce-products-partial" : "commerce-products", ct);
}
