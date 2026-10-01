using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_party: PartyManager::CreateParty: the player's lobby id, flat ({MatchID}). Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutCreateParty : PartyEndpoint
{
    protected override string Route => "create_party";

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.CreatePartyAsync(request, ct);
}
