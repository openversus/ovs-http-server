using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_mode_for_lobby: the lobby's mode (ModeString); the whole lobby when it has 2+ players. Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutSetModeForLobby : PartyEndpoint
{
    protected override string Route => "set_mode_for_lobby";

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.SetModeAsync(request, ct);
}
