using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /namechange.
/// Seen in: TS server: POST /namechange.
/// Server only.
/// </summary>
public sealed class PostNamechange : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/namechange");
    }
}
