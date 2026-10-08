using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /assets/openversus-update-required-keyart.png: the update popup's key art (TS server: cached 5 minutes).
/// </summary>
public sealed class GetAssetsOpenversusUpdateRequiredKeyartPng : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/assets/openversus-update-required-keyart.png");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendAsync(HttpContext, "openversus-update-required-keyart.png", "public, max-age=300");
}
