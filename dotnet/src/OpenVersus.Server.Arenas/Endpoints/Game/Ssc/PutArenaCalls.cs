using FastEndpoints;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Arenas.Endpoints.Game.Ssc;

/// <summary>
/// The Arena's own SSC calls, made between its rounds (character select, the shop, the next round's check-in): body keys
/// from the client binary (archive arenas-protocol.md), e.g. arena_select_character {ArenaLobbyId, CharacterSlug},
/// arena_player_shop_closed {ArenaLobbyId, ArenaRound, ShopPhaseDetails}, arena_checkin {ArenaParentId, ArenaRound}.
/// Each is answered by the run (<see cref="IArenaService.AnswerAsync"/>; which ones act on it: the header of
/// ArenaService.cs) and logged whole (Debug). Seen in: bench (2026-10-11: reroll, select, select absent, shop closed).
/// The TS server answers none of them.
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
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("{Call} with no session or no Redis; not answered", call);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        // The whole request, for reading what the game sends (it carries account ids: Debug, off unless Log:Level is raised).
        Logger.LogDebug("{Call} body: {Body}", call, body?.ToJsonString() ?? "none");
        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), body);
        await SendJsonAsync(LobbyDocuments.Ssc(await Resolve<IArenaService>().AnswerAsync(call, request, ct)), ct);
    }
}
