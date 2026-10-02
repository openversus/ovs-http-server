using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /favicon.ico.
/// Seen in: TS server: GET /favicon.ico.
/// Server only.
/// </summary>
public sealed class GetFaviconIco : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/favicon.ico");
    }
}
