using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /assets/openversus-update-required-thumbnail.png.
/// Seen in: TS server: GET /assets/openversus-update-required-thumbnail.png.
/// Server only.
/// </summary>
public sealed class GetAssetsOpenversusUpdateRequiredThumbnailPng : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/assets/openversus-update-required-thumbnail.png");
    }
}
