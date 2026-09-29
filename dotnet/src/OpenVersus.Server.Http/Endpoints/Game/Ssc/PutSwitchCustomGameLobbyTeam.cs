using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/switch_custom_game_lobby_team.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/switch_custom_game_lobby_team.
/// Ssc: binary.
/// </summary>
public sealed class PutSwitchCustomGameLobbyTeam : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/switch_custom_game_lobby_team");
    }
}
