using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_int_setting_for_custom_game.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_int_setting_for_custom_game.
/// Ssc: binary.
/// </summary>
public sealed class PutUpdateIntSettingForCustomGame : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_int_setting_for_custom_game");
    }
}
