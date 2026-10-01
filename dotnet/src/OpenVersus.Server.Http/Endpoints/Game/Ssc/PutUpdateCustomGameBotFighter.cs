namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_custom_game_bot_fighter: the leader changes a bot's character, skin and difficulty. Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_custom_game_bot_fighter.
/// </summary>
public sealed class PutUpdateCustomGameBotFighter : CustomLobbyEndpoint
{
    protected override string Route => "update_custom_game_bot_fighter";
}
