using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/leave_player_lobby: leaves the party lobby. Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutLeavePlayerLobby : PartyEndpoint
{
    protected override string Route => "leave_player_lobby";

    protected override bool Shared => true;

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.LeaveAsync(request, ct);
}
