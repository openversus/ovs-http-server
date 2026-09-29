using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /stats.
/// Seen in: TS server: GET /stats.
/// Server only.
/// </summary>
public sealed class GetStats : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/stats");
    }
}
