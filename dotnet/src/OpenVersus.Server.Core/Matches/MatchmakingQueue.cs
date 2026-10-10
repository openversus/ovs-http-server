using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A party's place in a matchmaking queue: what the TS websocket's party:queued and
// matchmaking:cancel handlers did (handlePartyQueued, cancelMatchMaking, stopMatchTick), done by whatever causes it, with
// the per-connection ticket and 1 s tick kept in Redis (realtime:queued) instead of the websocket's memory.
//
//   Queued (MatchmakingRequestService, after the request's answer): the ticket list's tickets that share a player with the
//     new one leave it (LREM); each player with a connection (realtime:conn:{player}) is set queued (player:{id} status),
//     sent OnMatchmakerStarted {MatchmakingRequestId} (payload match: the ticket's partyId) and given the ticket
//     (realtime:queued {player} = the ticket's JSON, which the gateway ticks every second: GatewayTicks); then the ticket is
//     pushed (RPUSH {matchType}), whoever is connected. A player not connected gets nothing, as there.
//   Cancelled (the game's cancel, a failed queueing, a player joining the party: "party-changed", an un-ready in a
//     searching party lobby of two players: PartyService.SetReadyAsync): each player holding a
//     ticket loses it: the ticket leaves its list (LREM by its bytes), the tick stops (HDEL), the game is sent
//     matchmaking-cancel {id: the cancel's id, state 3} and the player is set idle. A player holding none gets nothing, as
//     the TS handler did nothing without a running tick.
//   Found (the match flow, when it tells a launched match's players): each player's tick stops, and a player who held a
//     ticket is set in_match (the TS stopMatchTick wrote it only with a tick running).
//   Dropped (LobbyDisconnects: the player's game closed, or a newer login replaced its connection): the ticket the player
//     held, if it was queued before that, leaves its list, their tick stops and they are set idle; the others holding the
//     same ticket (the rest of the party) are cancelled as by the game's cancel, with the ticket's request id.
//
// Redis, written  {matchType} (LREM, RPUSH); player:{id} status; realtime:queued (HSET, HDEL)
// Redis, read     realtime:conn:{player}; realtime:queued
// Sent (ws:send)  OnMatchmakerStarted; matchmaking-cancel
//
// Unlike the TS websocket: a cancel takes the ticket out of whichever list it is on (TS: 1v1 and 2v2 only, so a Casual
// ticket outlived a disconnect); a second queueing replaces the tick (TS started a second interval and lost the first);
// a party member's dropped ticket is cancelled for the rest of the party, who are told (TS took it off the list and left
// their games searching for it, ticked every second, until they cancelled); a dropped player is set idle (TS: in_match,
// then deleted with the player's keys).

/// <summary>A party in a matchmaking queue (see the header of MatchmakingQueue.cs).</summary>
public static class MatchmakingQueue
{
    /// <summary>Who holds which ticket: player -> the ticket's JSON, as pushed.</summary>
    public const string QueuedKey = "realtime:queued";

    /// <summary>Queues <paramref name="ticket"/> (its exact JSON) for its players.</summary>
    public static async Task QueueAsync(IDatabase redis, string ticket)
    {
        var parsed = Js.Parse(ticket) as JsonObject ?? throw new InvalidOperationException("the ticket is not a JSON object");
        string list = (string?)parsed["matchType"] ?? throw new InvalidOperationException("the ticket names no queue");
        var players = PlayersOf(parsed);

        // Any ticket of these players already on this list goes (TS: a warning, then LREM of each).
        foreach (var raw in await redis.ListRangeAsync(list))
        {
            if (Js.Parse(raw.ToString()) is JsonObject other && PlayersOf(other).Intersect(players).Any())
            {
                await redis.ListRemoveAsync(list, raw);
            }
        }

        foreach (string player in players)
        {
            if (!await redis.KeyExistsAsync(GatewayPresence.ConnectionKey(player)))
            {
                continue;
            }

            await redis.HashSetAsync($"player:{player}", "status", "queued");
            await PlayerMessages.SendAsync(redis, [player], MatchmakerStarted(parsed));
            await redis.HashSetAsync(QueuedKey, player, ticket);
        }

        await redis.ListRightPushAsync(list, ticket);
    }

    /// <summary>Cancels the tickets <paramref name="players"/> hold; their games are told <paramref name="cancelId"/>.</summary>
    public static async Task CancelAsync(IDatabase redis, IEnumerable<string> players, JsonNode? cancelId)
    {
        foreach (string player in players.Distinct())
        {
            if ((string?)await redis.HashGetAsync(QueuedKey, player) is not { } ticket)
            {
                continue;
            }

            if ((Js.Parse(ticket) as JsonObject)?["matchType"] is JsonValue list && list.TryGetValue(out string? name))
            {
                await redis.ListRemoveAsync(name, ticket);
            }

            await redis.HashDeleteAsync(QueuedKey, player);
            await PlayerMessages.SendAsync(redis, [player], MatchLauncher.MatchmakingCancelled(cancelId?.DeepClone()));
            await redis.HashSetAsync($"player:{player}", "status", "idle");
        }
    }

    /// <summary>
    /// <paramref name="player"/>'s game is gone, at <paramref name="goneAtMs"/> (its close, or the login that replaced it):
    /// the ticket they hold, unless it was queued later (the new game's), leaves its list and they are set idle; the rest of
    /// their party is cancelled (<see cref="CancelAsync"/>). False when they held no such ticket.
    /// </summary>
    public static async Task<bool> DropAsync(IDatabase redis, string player, long goneAtMs)
    {
        if ((string?)await redis.HashGetAsync(QueuedKey, player) is not { } ticket)
        {
            return false;
        }

        var parsed = Js.Parse(ticket) as JsonObject;
        if (parsed?["created_at"] is JsonValue created && created.TryGetValue(out long seconds) && seconds * 1000 > goneAtMs)
        {
            return false;
        }

        if (parsed?["matchType"] is JsonValue list && list.TryGetValue(out string? name))
        {
            await redis.ListRemoveAsync(name, ticket);
        }

        await redis.HashDeleteAsync(QueuedKey, player);
        await redis.HashSetAsync($"player:{player}", "status", "idle");
        var others = new List<string>();
        foreach (string other in parsed is null ? [] : PlayersOf(parsed).Where(p => p != player).Distinct())
        {
            if ((string?)await redis.HashGetAsync(QueuedKey, other) == ticket)
            {
                others.Add(other);
            }
        }

        await CancelAsync(redis, others, RequestIdOf(ticket));
        return true;
    }

    /// <summary>A match was found for <paramref name="players"/>: their ticks stop, and those who held a ticket are in_match.</summary>
    public static async Task FoundAsync(IDatabase redis, IEnumerable<string> players)
    {
        foreach (string player in players.Distinct())
        {
            if (await redis.HashDeleteAsync(QueuedKey, player))
            {
                await redis.HashSetAsync($"player:{player}", "status", "in_match");
            }
        }
    }

    /// <summary>The ticket one of <paramref name="players"/> holds (the first found), as pushed; null when none holds one.</summary>
    public static async Task<string?> HeldTicketAsync(IDatabase redis, IEnumerable<string> players)
    {
        foreach (string player in players.Distinct())
        {
            if ((string?)await redis.HashGetAsync(QueuedKey, player) is { } ticket)
            {
                return ticket;
            }
        }

        return null;
    }

    /// <summary>A ticket's request id, for the tick; null when the ticket names none.</summary>
    public static JsonNode? RequestIdOf(string ticket) => (Js.Parse(ticket) as JsonObject)?["matchmakingRequestId"]?.DeepClone();

    /// <summary>OnMatchmakerStarted, as the TS websocket's handlePartyQueued sent it.</summary>
    public static JsonObject MatchmakerStarted(JsonObject ticket)
    {
        // undefined in TS when the game sent no match (the ticket has no partyId), which its Hydra encoder writes as null.
        return PlayerMessages.Update(new JsonObject
        {
            ["template_id"] = "OnMatchmakerStarted",
            ["MatchmakingRequestId"] = ticket["matchmakingRequestId"]?.DeepClone(),
        }, new JsonObject { ["match"] = new JsonObject { ["id"] = ticket["partyId"]?.DeepClone() } });
    }

    /// <summary>matchmaking-tick, as the TS websocket's handleMatchTick sent it every second.</summary>
    public static JsonObject Tick(JsonNode? requestId) => new()
    {
        ["data"] = new JsonObject(),
        ["payload"] = new JsonObject { ["id"] = requestId?.DeepClone(), ["state"] = 2 },
        ["header"] = "matchmaking-tick",
        ["cmd"] = "matchmaking-tick",
    };

    private static List<string> PlayersOf(JsonObject ticket) =>
        [.. (ticket["players"] as JsonArray ?? []).Select(p => p?["id"] is JsonValue v && v.TryGetValue(out string? id) ? id : null).OfType<string>()];
}
