using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/switch_custom_game_lobby_team: a player moves to another team, or to or from the spectators. Seen in: binary ssc name; TS server: PUT /ssc/invoke/switch_custom_game_lobby_team.
/// </summary>
public sealed class PutSwitchCustomGameLobbyTeam : CustomLobbyEndpoint
{
    protected override string Route => "switch_custom_game_lobby_team";

    // In an Arena lobby, the Arena service's (the lobby screen shares the custom lobby's verbs).
    protected override Task<JsonObject?> OtherLobbyAsync(PartyRequest request, CancellationToken ct) =>
        Resolve<IArenaLobbyService>().SwitchTeamAsync(request.AccountId, request.Body, ct);
}
