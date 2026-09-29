using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/promote_to_lobby_leader.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/promote_to_lobby_leader.
/// Ssc: binary.
/// </summary>
public sealed class PutPromoteToLobbyLeader : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/promote_to_lobby_leader");
    }
}
