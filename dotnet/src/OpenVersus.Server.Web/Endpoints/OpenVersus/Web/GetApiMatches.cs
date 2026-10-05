using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

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
