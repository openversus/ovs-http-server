using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lock_lobby_loadout: locks the player's character and skin, told to the rest of the party. Seen in: binary ssc name; captured 18x; TS server.
/// </summary>
public sealed class PutLockLobbyLoadout : PartyEndpoint
{
    protected override string Route => "lock_lobby_loadout";

    protected override bool Shared => true;

    protected override Task<JsonObject> AnswerAsync(IPartyService party, PartyRequest request, CancellationToken ct) => party.LockLoadoutAsync(request, ct);
}
