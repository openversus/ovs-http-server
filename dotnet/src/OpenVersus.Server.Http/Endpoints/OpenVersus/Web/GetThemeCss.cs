using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /theme.css.
/// Seen in: TS server: GET /theme.css.
/// Server only.
/// </summary>
public sealed class GetThemeCss : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/theme.css");
    }
}
