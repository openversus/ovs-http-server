namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_team_style_for_custom_game: the leader sets the team style (and the mode it gives). Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_team_style_for_custom_game.
/// </summary>
public sealed class PutUpdateTeamStyleForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "update_team_style_for_custom_game";
}
