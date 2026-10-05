using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.VirtualCommerce;

/// <summary>
/// POST /virtual_commerce/purchases/{id}/{item}.
/// Seen in: binary 0x145069300; TS server: POST /virtual_commerce/purchases/697fa194ce48c5be8a71abf4/toasts_gleamium, POST /virtual_commerce/purchases/{id}/toasts_gleamium.
/// Item e.g. toasts_gleamium.
/// </summary>
public sealed class PostVirtualCommercePurchasesByIdByItem : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/virtual_commerce/purchases/{id}/{item}");
    }
}
