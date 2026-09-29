using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// POST /iam/v3/oauth/verify.
/// Seen in: TS server: POST /iam/v3/oauth/verify.
/// Server only.
/// </summary>
public sealed class PostIamV3OauthVerify : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/iam/v3/oauth/verify");
    }
}
