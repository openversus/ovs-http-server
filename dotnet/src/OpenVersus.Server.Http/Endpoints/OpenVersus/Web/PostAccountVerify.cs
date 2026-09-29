using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

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
