namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/join_custom_game_lobby: joins a custom lobby, as a player or a spectator. Seen in: binary ssc name; TS server: PUT /ssc/invoke/join_custom_game_lobby.
/// </summary>
public sealed class PutJoinCustomGameLobby : CustomLobbyEndpoint
{
    protected override string Route => "join_custom_game_lobby";
}
