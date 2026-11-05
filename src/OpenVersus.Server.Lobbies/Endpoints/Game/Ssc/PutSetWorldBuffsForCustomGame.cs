namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_world_buffs_for_custom_game: the leader picks the world buffs (the mode's required ones stay). Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_world_buffs_for_custom_game.
/// </summary>
public sealed class PutSetWorldBuffsForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "set_world_buffs_for_custom_game";
}
