using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lobby_code: the leader gets a code others can join the lobby by (GET /matches/{code}); in an Arena
/// lobby (<see cref="IArenaLobbyService"/>) any member does (the lobby screen's eye button). Seen in: binary ssc name; TS
/// server: PUT /ssc/invoke/lobby_code; bench (Arena lobby, 2026-10-10).
/// </summary>
public sealed class PutLobbyCode : CustomLobbyEndpoint
{
    protected override string Route => "lobby_code";

    protected override Task<JsonObject?> OtherLobbyAsync(PartyRequest request, CancellationToken ct) =>
        Resolve<IArenaLobbyService>().LobbyCodeAsync(request.AccountId, request.Body, ct);
}
