using FastEndpoints;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// The Arena's own SSC calls, made between its rounds (character select, the shop, the next round's check-in): body keys
/// from the client binary (archive arenas-protocol.md), e.g. arena_select_character {ArenaLobbyId, CharacterSlug},
/// arena_player_shop_closed {ArenaLobbyId, ArenaRound, ShopPhaseDetails}, arena_checkin {ArenaParentId, ArenaRound}.
/// INSTRUMENTATION ONLY, for now: each is logged whole (Debug) and answered as a success (FMvsArenaBaseResponse
/// {bSuccess, playerId}), so the game goes on and its next call is seen. None is acted on yet.
/// Seen in: the client binary (2026-10-10); not yet on the wire. The TS server answers none of them.
/// </summary>
public sealed class PutArenaCalls : JsonBodyEndpoint
{
    /// <summary>The calls this endpoint answers.</summary>
    public static readonly string[] Names =
    [
        "arena_select_character", "arena_select_character_absent", "arena_reroll_characters", "arena_purchase_item", "arena_sell_item",
        "arena_reroll_shop", "arena_player_shop_closed", "arena_shop_absent", "arena_checkin", "arena_match_absent",
    ];

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes([.. Names.Select(n => $"/ssc/invoke/{n}")]);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = (await ReadBodyAsync(ct))?.AsObject();
        string call = HttpContext.Request.Path.Value?.Split('/').LastOrDefault() ?? "";
        var session = HttpContext.Session();
        Logger.LogInformation("{Call} from {Player} (instrumentation: answered success, not acted on)", call, session?.AccountId);
        // The whole request, for reading what the game sends (it carries account ids: Debug, off unless Log:Level is raised).
        Logger.LogDebug("{Call} body: {Body}", call, body?.ToJsonString() ?? "none");
        await SendJsonAsync(LobbyDocuments.Ssc(new JsonObject { ["bSuccess"] = true, ["playerId"] = session?.AccountId }), ct);
    }
}
