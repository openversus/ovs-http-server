using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /namechange.
/// Seen in: TS server: GET /namechange.
/// Server only.
/// </summary>
public sealed class GetNamechange : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/namechange");
    }
}
