using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /images/{image}.
/// Seen in: TS server: GET /images/{image}.
/// Server only.
/// </summary>
public sealed class GetImagesByImage : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/images/{image}");
    }
}
