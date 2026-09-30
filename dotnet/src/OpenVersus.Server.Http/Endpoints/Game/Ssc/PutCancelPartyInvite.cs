using System.Text.Json.Nodes;
using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/cancel_party_invite: answered with an empty body and nothing done, as the TS server does (ssc/routes.ts).
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/cancel_party_invite.
/// Follow-up: docs/SSC.md (what the game sends, and what is left to do).
/// </summary>
public sealed class PutCancelPartyInvite : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/cancel_party_invite");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
}
