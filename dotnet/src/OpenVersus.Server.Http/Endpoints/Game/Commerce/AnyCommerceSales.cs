using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// Any method /commerce/sales.
/// Seen in: binary fragment.
/// Binary fragment; no builder found yet.
/// </summary>
public sealed class AnyCommerceSales : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/commerce/sales");
    }
}
