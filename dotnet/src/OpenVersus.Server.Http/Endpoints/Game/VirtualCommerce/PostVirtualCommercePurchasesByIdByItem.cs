using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.VirtualCommerce;

/// <summary>
/// POST /virtual_commerce/purchases/{id}/{item}: a store purchase. toasts_gleamium for a 24-character id is answered {}
/// as the TS server answers it (its only purchase route, a regex on the id's length); every other item has no TS
/// route (its catch-all). Nothing is sold: the store is for a later day (earned currency, never money), and until
/// then a purchase must at least not crash the game, which is RE work (PORT-TAIL.md).
/// Seen in: binary 0x145069300; TS server: POST /virtual_commerce/purchases/697fa194ce48c5be8a71abf4/toasts_gleamium, POST /virtual_commerce/purchases/{id}/toasts_gleamium.
/// </summary>
public sealed class PostVirtualCommercePurchasesByIdByItem : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/virtual_commerce/purchases/{id}/{item}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("item") == "toasts_gleamium" && Route<string>("id") is { Length: 24 }
            ? SendJsonAsync(new JsonObject(), ct)
            : SendTsCatchAllAsync(ct);
}
