using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/invite_to_player_lobby.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/invite_to_player_lobby.
/// Ssc: server/capture.
/// </summary>
public sealed class PutInviteToPlayerLobby : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/invite_to_player_lobby");
    }
}
