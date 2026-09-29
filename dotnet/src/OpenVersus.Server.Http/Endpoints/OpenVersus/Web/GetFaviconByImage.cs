using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /favicon/{image}.
/// Seen in: TS server: GET /favicon/{image}.
/// Server only.
/// </summary>
public sealed class GetFaviconByImage : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/favicon/{image}");
    }
}
