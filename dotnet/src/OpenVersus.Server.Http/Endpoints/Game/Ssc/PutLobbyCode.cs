namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lobby_code: the leader gets a code others can join the lobby by (GET /matches/{code}). Seen in: binary ssc name; TS server: PUT /ssc/invoke/lobby_code.
/// </summary>
public sealed class PutLobbyCode : CustomLobbyEndpoint
{
    protected override string Route => "lobby_code";
}
