using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Social;

/// <summary>
/// PUT /social/me/block/{id}.
/// Seen in: binary 0x140f93530; TS server: PUT /social/me/block/{blockid}.
/// Social layer; method from server.
/// </summary>
public sealed class PutSocialMeBlockById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/social/me/block/{id}");
    }
}
