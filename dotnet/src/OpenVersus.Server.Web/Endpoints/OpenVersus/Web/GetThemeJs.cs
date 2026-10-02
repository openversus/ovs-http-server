using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /theme.js.
/// Seen in: TS server: GET /theme.js.
/// Server only.
/// </summary>
public sealed class GetThemeJs : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/theme.js");
    }
}
