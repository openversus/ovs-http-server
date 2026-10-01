namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/reset_custom_lobby_to_defaults: the leader resets the lobby to its mode's settings. Seen in: binary ssc name; TS server: PUT /ssc/invoke/reset_custom_lobby_to_defaults.
/// </summary>
public sealed class PutResetCustomLobbyToDefaults : CustomLobbyEndpoint
{
    protected override string Route => "reset_custom_lobby_to_defaults";
}
