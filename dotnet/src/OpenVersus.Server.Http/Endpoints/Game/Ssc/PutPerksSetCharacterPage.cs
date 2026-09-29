using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_set_character_page.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/perks_set_character_page.
/// Ssc: server/capture.
/// </summary>
public sealed class PutPerksSetCharacterPage : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_set_character_page");
    }
}
