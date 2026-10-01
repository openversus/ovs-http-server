namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_enabled_maps_for_custom_game: the leader picks the maps. Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_enabled_maps_for_custom_game.
/// </summary>
public sealed class PutSetEnabledMapsForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "set_enabled_maps_for_custom_game";
}
