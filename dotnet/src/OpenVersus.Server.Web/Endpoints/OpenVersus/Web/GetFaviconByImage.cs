using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /favicon/{image}: the TS server's image handler, its one list for /favicon/ and /images/; anything else 404.
/// </summary>
public sealed class GetFaviconByImage : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/favicon/{image}");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendImageAsync(HttpContext, Route<string>("image", isRequired: false));
}
