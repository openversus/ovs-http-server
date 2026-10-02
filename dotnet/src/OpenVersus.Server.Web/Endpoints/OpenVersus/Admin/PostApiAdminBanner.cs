using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// POST /api/admin/banner.
/// Seen in: TS server: POST /api/admin/banner.
/// Server only.
/// </summary>
public sealed class PostApiAdminBanner : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/admin/banner");
    }
}
