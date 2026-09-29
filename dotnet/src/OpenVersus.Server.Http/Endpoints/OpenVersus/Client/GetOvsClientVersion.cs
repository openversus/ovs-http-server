using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/client-version.
/// Seen in: captured 10x; TS server: GET /ovs/client-version.
/// Capture only; server only.
/// </summary>
public sealed class GetOvsClientVersion : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/client-version");
    }
}
