using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/add_custom_game_bot.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/add_custom_game_bot.
/// Ssc: binary.
/// </summary>
public sealed class PutAddCustomGameBot : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/add_custom_game_bot");
    }
}
