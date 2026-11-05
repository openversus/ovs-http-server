namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_game_mode_for_custom_game: the leader sets the game mode. Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_game_mode_for_custom_game.
/// </summary>
public sealed class PutSetGameModeForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "set_game_mode_for_custom_game";
}
