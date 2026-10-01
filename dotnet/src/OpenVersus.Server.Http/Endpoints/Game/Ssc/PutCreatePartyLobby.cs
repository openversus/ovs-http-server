using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_party_lobby: the player's party lobby: the one they are in with others when everyone is online, else a new solo one. Seen in: binary ssc name; captured 11x; TS server.
/// </summary>
public sealed class PutCreatePartyLobby : PartyEndpoint
{
    protected override string Route => "create_party_lobby";

    protected override bool Shared => true;

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.CreatePartyLobbyAsync(request, ct);
}
