using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/invite_to_player_lobby: invites a player to the party (InviteReceivedForLobby on their websocket). Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutInviteToPlayerLobby : PartyEndpoint
{
    protected override string Route => "invite_to_player_lobby";

    protected override bool Shared => true;

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.InviteAsync(request, ct);
}
