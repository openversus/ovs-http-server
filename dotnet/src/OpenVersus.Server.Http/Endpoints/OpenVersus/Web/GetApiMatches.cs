using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /api/matches.
/// Seen in: TS server: GET /api/matches.
/// Server only.
/// </summary>
public sealed class GetApiMatches : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/matches");
    }
}
