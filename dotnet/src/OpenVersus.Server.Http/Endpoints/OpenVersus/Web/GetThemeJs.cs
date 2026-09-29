using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

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
