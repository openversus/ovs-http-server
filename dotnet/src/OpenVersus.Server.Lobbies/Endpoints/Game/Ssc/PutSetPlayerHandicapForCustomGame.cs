namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_player_handicap_for_custom_game: a player sets their own handicap. Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_player_handicap_for_custom_game.
/// </summary>
public sealed class PutSetPlayerHandicapForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "set_player_handicap_for_custom_game";
}
