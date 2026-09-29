using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// GET /social/me/blocked.
/// Seen in: binary 0x140f99e00; captured 14x; TS server: GET /social/me/blocked.
/// Social layer; method from capture.
/// </summary>
public sealed class GetSocialMeBlocked : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/social/me/blocked");
    }
}
