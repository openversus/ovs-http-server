using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /theme.css: the website's stylesheet (TS server: GET /theme.css).
/// </summary>
public sealed class GetThemeCss : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/theme.css");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendAsync(HttpContext, "theme.css");
}
