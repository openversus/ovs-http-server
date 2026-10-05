using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /account/verify.
/// Seen in: TS server: POST /account/verify.
/// Server only.
/// </summary>
public sealed class PostAccountVerify : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/account/verify");
    }
}
