using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

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
