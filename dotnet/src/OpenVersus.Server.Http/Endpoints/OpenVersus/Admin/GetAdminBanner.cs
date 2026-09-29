using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Admin;

/// <summary>
/// GET /admin/banner.
/// Seen in: TS server: GET /admin/banner.
/// Server only.
/// </summary>
public sealed class GetAdminBanner : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/admin/banner");
    }
}
