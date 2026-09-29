using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_custom_game_bot_fighter.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_custom_game_bot_fighter.
/// Ssc: binary.
/// </summary>
public sealed class PutUpdateCustomGameBotFighter : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_custom_game_bot_fighter");
    }
}
