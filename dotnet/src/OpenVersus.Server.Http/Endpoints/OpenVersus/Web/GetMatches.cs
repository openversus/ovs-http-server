using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /matches.
/// Seen in: TS server: GET /matches.
/// Server only.
/// </summary>
public sealed class GetMatches : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches");
    }
}
