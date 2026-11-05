using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /theme.js: the website's theme switch (TS server: GET /theme.js).
/// </summary>
public sealed class GetThemeJs : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/theme.js");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendAsync(HttpContext, "theme.js");
}
