namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/switch_custom_game_lobby_team: a player moves to another team, or to or from the spectators. Seen in: binary ssc name; TS server: PUT /ssc/invoke/switch_custom_game_lobby_team.
/// </summary>
public sealed class PutSwitchCustomGameLobbyTeam : CustomLobbyEndpoint
{
    protected override string Route => "switch_custom_game_lobby_team";
}
