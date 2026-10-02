using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /api/identify.
/// Seen in: captured 10x; TS server: POST /api/identify.
/// Capture only; server only.
/// </summary>
public sealed class PostApiIdentify : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/identify");
    }
}
