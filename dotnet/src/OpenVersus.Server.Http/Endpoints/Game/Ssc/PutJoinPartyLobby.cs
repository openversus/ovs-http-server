using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/join_party_lobby: joins the party the player was invited to (or their own). Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutJoinPartyLobby : PartyEndpoint
{
    protected override string Route => "join_party_lobby";

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.JoinAsync(request, ct);
}
