namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_custom_game_lobby: creates a custom lobby led by the player (2v2 classic, everything selected). Seen in: binary ssc name; TS server: PUT /ssc/invoke/create_custom_game_lobby.
/// </summary>
public sealed class PutCreateCustomGameLobby : CustomLobbyEndpoint
{
    protected override string Route => "create_custom_game_lobby";
}
