using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /images/{image}: the TS server's image handler, its one list for /images/ and /favicon/; anything else 404.
/// </summary>
public sealed class GetImagesByImage : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/images/{image}");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendImageAsync(HttpContext, Route<string>("image", isRequired: false));
}
