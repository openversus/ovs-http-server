using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /favicon.ico (TS server: GET /favicon.ico, through its image handler).
/// </summary>
public sealed class GetFaviconIco : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/favicon.ico");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendImageAsync(HttpContext, "favicon.ico");
}
