using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /account/switch.
/// Seen in: TS server: POST /account/switch.
/// Server only.
/// </summary>
public sealed class PostAccountSwitch : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/account/switch");
    }
}
