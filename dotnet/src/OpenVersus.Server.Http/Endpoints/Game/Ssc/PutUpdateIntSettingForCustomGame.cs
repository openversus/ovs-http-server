namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_int_setting_for_custom_game: the leader changes one match setting. Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_int_setting_for_custom_game.
/// </summary>
public sealed class PutUpdateIntSettingForCustomGame : CustomLobbyEndpoint
{
    protected override string Route => "update_int_setting_for_custom_game";
}
