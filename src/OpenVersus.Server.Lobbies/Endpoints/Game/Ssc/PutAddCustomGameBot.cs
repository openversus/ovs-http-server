namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/add_custom_game_bot: the leader adds a bot to a team. Seen in: binary ssc name; TS server: PUT /ssc/invoke/add_custom_game_bot.
/// </summary>
public sealed class PutAddCustomGameBot : CustomLobbyEndpoint
{
    protected override string Route => "add_custom_game_bot";
}
