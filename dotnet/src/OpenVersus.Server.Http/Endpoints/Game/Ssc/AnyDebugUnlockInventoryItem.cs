using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/debug_unlock_inventory_item.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyDebugUnlockInventoryItem : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/debug_unlock_inventory_item");
    }
}
