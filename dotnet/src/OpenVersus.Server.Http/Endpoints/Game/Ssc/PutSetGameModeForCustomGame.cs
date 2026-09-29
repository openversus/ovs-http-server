using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_game_mode_for_custom_game.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_game_mode_for_custom_game.
/// Ssc: binary.
/// </summary>
public sealed class PutSetGameModeForCustomGame : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_game_mode_for_custom_game");
    }
}
