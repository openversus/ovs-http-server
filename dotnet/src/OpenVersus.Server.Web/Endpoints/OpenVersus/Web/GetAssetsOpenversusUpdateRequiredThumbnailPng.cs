using FastEndpoints;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /assets/openversus-update-required-thumbnail.png: the update popup's thumbnail (TS server: cached 5 minutes).
/// </summary>
public sealed class GetAssetsOpenversusUpdateRequiredThumbnailPng : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/assets/openversus-update-required-thumbnail.png");
    }

    public override Task HandleAsync(CancellationToken ct) => StaticFiles.SendAsync(HttpContext, "openversus-update-required-thumbnail.png", "public, max-age=300");
}
