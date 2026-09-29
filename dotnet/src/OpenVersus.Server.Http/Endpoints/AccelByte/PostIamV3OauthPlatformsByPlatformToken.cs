using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// POST /iam/v3/oauth/platforms/{platform}/token.
/// Seen in: TS server: POST /iam/v3/oauth/platforms/{platform}/token.
/// Server only.
/// </summary>
public sealed class PostIamV3OauthPlatformsByPlatformToken : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/iam/v3/oauth/platforms/{platform}/token");
    }
}
