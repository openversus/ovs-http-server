namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/start_custom_match: the leader starts the match. Seen in: binary ssc name; TS server: PUT /ssc/invoke/start_custom_match.
/// </summary>
public sealed class PutStartCustomMatch : CustomLobbyEndpoint
{
    protected override string Route => "start_custom_match";
}
