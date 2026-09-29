using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Social;

/// <summary>
/// PUT /social/me/unblock/{id}.
/// Seen in: binary 0x140fa3540; TS server: PUT /social/me/unblock/{blockid}.
/// Social layer; method from server.
/// </summary>
public sealed class PutSocialMeUnblockById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/social/me/unblock/{id}");
    }
}
