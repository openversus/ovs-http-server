using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_lobby_not_joinable: marks the lobby not joinable (matchmaking started, training mode). Seen in: binary ssc name; captured 1x; TS server.
/// </summary>
public sealed class PutSetLobbyNotJoinable : PartyEndpoint
{
    protected override string Route => "set_lobby_not_joinable";

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.SetNotJoinableAsync(request, ct);
}
