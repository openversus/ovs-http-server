namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/kick_from_lobby: the leader removes a player or a bot. Seen in: binary ssc name; TS server: PUT /ssc/invoke/kick_from_lobby.
/// </summary>
public sealed class PutKickFromLobby : CustomLobbyEndpoint
{
    protected override string Route => "kick_from_lobby";
}
