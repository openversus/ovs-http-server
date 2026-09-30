using System.Text.Json.Nodes;
using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_party_game_modes: answered with an empty body and nothing kept, as the TS server does (ssc/routes.ts).
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_party_game_modes.
/// Follow-up: docs/SSC.md (what the game sends, and what is left to do).
/// </summary>
public sealed class PutUpdatePartyGameModes : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_party_game_modes");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
}
