using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Admin;

/// <summary>
/// GET /api/admin/banner/online-count.
/// Seen in: TS server: GET /api/admin/banner/online-count.
/// Server only.
/// </summary>
public sealed class GetApiAdminBannerOnlineCount : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/admin/banner/online-count");
    }
}
