using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_party_game_modes.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_party_game_modes.
/// Ssc: binary.
/// </summary>
public sealed class PutUpdatePartyGameModes : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_party_game_modes");
    }
}
