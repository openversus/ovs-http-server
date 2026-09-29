using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// Any method /commerce/purchases/{id}/finalize.
/// Seen in: binary 0x144fd91b0.
/// Method not read yet; a bare '/' piece may mean one more segment.
/// </summary>
public sealed class AnyCommercePurchasesByIdFinalize : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/commerce/purchases/{id}/finalize");
    }
}
