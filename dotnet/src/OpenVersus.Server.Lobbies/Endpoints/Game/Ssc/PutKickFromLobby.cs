using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/kick_from_lobby: the leader removes a player or a bot. Seen in: binary ssc name; TS server: PUT /ssc/invoke/kick_from_lobby.
/// </summary>
public sealed class PutKickFromLobby : CustomLobbyEndpoint
{
    protected override string Route => "kick_from_lobby";

    // In an Arena lobby, the Arena service's (the lobby screen shares the custom lobby's verbs).
    protected override Task<JsonObject?> OtherLobbyAsync(PartyRequest request, CancellationToken ct) =>
        Resolve<IArenaLobbyService>().KickAsync(request.AccountId, request.Body, ct);
}
