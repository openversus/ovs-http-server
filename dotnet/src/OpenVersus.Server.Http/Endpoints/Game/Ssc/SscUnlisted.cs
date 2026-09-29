using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any SSC function without an endpoint of its own. The game's SSC names cannot all be enumerated from the binary
/// (see docs/ROUTES.md), so a name the route map is missing lands here and shows up in the log by name.
/// </summary>
public sealed class SscUnlisted : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/{name}");
    }
}
