using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_ready_for_lobby: the player's ready state, told to the rest of the party. Seen in: binary ssc name; captured 10x; TS server.
/// </summary>
public sealed class PutSetReadyForLobby : PartyEndpoint
{
    protected override string Route => "set_ready_for_lobby";

    protected override bool Shared => true;

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.SetReadyAsync(request, ct);
}
