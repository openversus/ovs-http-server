using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_lobby_joinable: marks the lobby joinable again (after set_lobby_not_joinable); the answer is empty
/// (the TS server's, which kept nothing). Seen in: binary ssc name; captured 5x; TS server.
/// </summary>
public sealed class PutSetLobbyJoinable : PartyEndpoint
{
    protected override string Route => "set_lobby_joinable";

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.SetJoinableAsync(request, ct);
}
