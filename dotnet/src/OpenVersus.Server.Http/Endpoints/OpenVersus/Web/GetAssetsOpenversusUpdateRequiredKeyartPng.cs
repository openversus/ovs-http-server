using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /assets/openversus-update-required-keyart.png.
/// Seen in: TS server: GET /assets/openversus-update-required-keyart.png.
/// Server only.
/// </summary>
public sealed class GetAssetsOpenversusUpdateRequiredKeyartPng : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/assets/openversus-update-required-keyart.png");
    }
}
