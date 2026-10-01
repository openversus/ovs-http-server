namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/promote_to_lobby_leader: the leader hands the lead to another player. Seen in: binary ssc name; TS server: PUT /ssc/invoke/promote_to_lobby_leader.
/// </summary>
public sealed class PutPromoteToLobbyLeader : CustomLobbyEndpoint
{
    protected override string Route => "promote_to_lobby_leader";
}
