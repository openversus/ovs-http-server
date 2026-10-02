using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Leaderboards;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// POST /matches/matchmaking/{criteria}/request and POST /matches/matchmaking/request/{id}/cancel, ported from the TS
// server's handleMatches_matchmaking_1v1_retail_request, handleMatches_matchmaking_2v2_retail_request and
// handle_cancel_matchmaking (handlers/matches.ts) with queueMatch and cancelMatchmakingForAll (services/matchmakingService.ts).
//
// Criteria: 1v1-retail and ranked-1v1-retail are both the 1v1 request (the answer says 1v1-retail, the ticket 1v1: the
// TS server has no ranked queue); 2v2-retail the 2v2 one. A 1v1 request from a lobby of two or more is the 2v2 request.
//
// The request, in the TS server's order:
//   1v1: the requester's client must be current (else the update modal and the gate's failure body, 200)
//   both: the requester's tickets leave the 1v1 and 2v2 lists (LREM, silently), and a ranked set they were in is ended
//   both: the requester's session (connections:{id}); when player:{id}:cosmetics is missing (never equipped), the
//         equipped cosmetics become the match copy (connections:{id}:cosmetics); no player:{id} character or skin: 500
//         {error: "player_loadout_not_found"}; else the loadout becomes the session's (PlayedLoadout)
//   2v2: everyone in the requester's lobby: current clients (else modal + failure body), ranked sets ended, every
//        teammate connected (else 200 {error: "Not all party members are connected"}), each teammate's loadout the
//        session's when the session has an address
//   both: the answer (the Hydra matchmaking request), THEN the ticket is published on party:queued, which the TS
//         websocket turns into OnMatchmakerStarted for each player and pushes onto the 1v1 or 2v2 list for the
//         matchmaker. The answer goes first, as there: the game learns the request id from it.
// The ticket's bytes matter: the matchmaker removes a matched ticket with LREM of JSON.stringify(JSON.parse(ticket)), so
// its keys are the TS queueMatch's, in its order, and a player's ip is left out (not null) when player:{id} has none.
// Each player's skill is their rating for the character in player:{id} (eloratings characters_1v1/characters_2v2, made
// with the default rating when missing, as getOrCreateRating), 0 when there is none or Mongo fails. partyId is the
// request's match (the game's lobby match), not the lobby id.
//
// Cancel: matchmaking:cancel {playersIds: everyone in the requester's lobby (or the requester), matchmakingId}, which the
// TS websocket acts on; each of those players' party_ready:{lobby} is deleted. Answer {body: {}, metadata: null,
// return_code: 0}.
//
// Not ported: queueMatch's leaveLobby from services/customLobbyService.ts (the custom lobbies of the web UI, retired
// 2026-09-26): nothing has made those lobbies since (no code imports its createLobby or joinLobby; no
// custom_lobby_player:* key on the bench), so it never had a lobby to leave. The game's custom lobbies are
// CustomLobbyService's.
// Unlike there: a teammate's loadout is copied only when they have a character and a skin (TS wrote undefined fields);
// a request with no data answers with null MultiplayParams and cluster (TS threw before answering).

/// <summary>An answer, and what happens once it has been sent (the ticket is published after the game has the answer).</summary>
public sealed record MatchmakingAnswer(int Status, JsonNode Body, Func<Task>? After = null);

public interface IMatchmakingRequestService
{
    /// <summary>The matchmaking request for <paramref name="criteria"/>; null for a criteria the game is not answered for.</summary>
    Task<MatchmakingAnswer?> RequestAsync(string criteria, PartyRequest request, CancellationToken ct);

    /// <summary>Cancels matchmaking request <paramref name="requestId"/> for the requester's lobby.</summary>
    Task<JsonObject> CancelAsync(string requestId, PartyRequest request, CancellationToken ct);
}

internal sealed class MatchmakingRequestService(IServiceProvider services, IClientUpdateGate gate, ICosmeticsService cosmetics, EloRatings ratings,
    IOptionsMonitor<LobbySettings> settings, TimeProvider time, ILogger<MatchmakingRequestService> log) : IMatchmakingRequestService
{
    public const string QueuedChannel = "party:queued";

    private static readonly JsonObject s_founders = new()
    {
        ["FoundersPack3"] = true,
        ["FoundersPack3_steam"] = true,
        ["founderpackcoolnameflag"] = true,
        ["closed_alpha_battlepass_completed"] = true,
    };

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    public async Task<MatchmakingAnswer?> RequestAsync(string criteria, PartyRequest request, CancellationToken ct)
    {
        switch (criteria)
        {
            case "1v1-retail" or "ranked-1v1-retail":
                return await OneVersusOneAsync(request, ct);
            case "2v2-retail":
                return await TwoVersusTwoAsync(request, ct);
            default:
                return null;
        }
    }

    private async Task<MatchmakingAnswer> OneVersusOneAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        log.LogInformation("Received 1v1 retail matchmaking request");

        var outdated = await gate.RequiringUpdateAsync([me]);
        if (outdated.Count > 0)
        {
            await gate.RequestModalsAsync(outdated.Select(o => o.AccountId));
            log.LogWarning("Blocked 1v1 matchmaking for outdated client {Player}", me);
            return new MatchmakingAnswer(200, gate.FailureBody());
        }

        await RemoveTicketsAsync(redis, me);
        await EndRankedSetAsync(redis, me);

        if (await LobbyPlayersAsync(redis, me) is { Count: >= 2 } players)
        {
            log.LogInformation("Lobby of {Player} has {Count} players, redirecting 1v1 request to 2v2 handler", me, players.Count);
            return await TwoVersusTwoAsync(request, ct);
        }

        var (id, profile, failure) = await PrepareRequesterAsync(redis, request, ct);
        if (failure is not null)
        {
            return failure;
        }

        var answer = Answer(request, id, "1v1-retail", 1, 606.406234735998, "character_wonder_woman", s_founders.DeepClone(),
            new JsonObject { [id] = Region(0.04239736124873161) },
            new JsonObject { [id] = new JsonArray(Guid.NewGuid().ToString()) },
            new JsonObject { [id] = OneVersusOnePlayer(id, profile) },
            new JsonObject { [id] = new JsonArray() },
            partyId: null, profileId: "1252922", idFirst: false);
        return new MatchmakingAnswer(200, answer, () => QueueAsync(redis, id, [id], request.Body?["match"], (string)answer["id"]!, "1v1"));
    }

    private async Task<MatchmakingAnswer> TwoVersusTwoAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        log.LogInformation("Received 2v2 retail matchmaking request");

        await RemoveTicketsAsync(redis, me);
        await EndRankedSetAsync(redis, me);

        var (id, profile, failure) = await PrepareRequesterAsync(redis, request, ct);
        if (failure is not null)
        {
            return failure;
        }

        string? lobbyId = (string?)await redis.StringGetAsync($"player_lobby:{id}");
        var lobbyPlayers = lobbyId is null ? null : await PlayersOfAsync(redis, lobbyId);
        var all = lobbyPlayers ?? [id];

        var outdated = await gate.RequiringUpdateAsync(all);
        if (outdated.Count > 0)
        {
            await gate.RequestModalsAsync(outdated.Select(o => o.AccountId));
            log.LogWarning("Blocked 2v2 matchmaking because update is required for: {Players}",
                string.Join(", ", outdated.Select(o => $"{o.AccountId}:{(o.ClientVersion.Length > 0 ? o.ClientVersion : "legacy")}")));
            return new MatchmakingAnswer(200, gate.FailureBody());
        }

        foreach (string pid in all.Where(p => p != id))
        {
            await EndRankedSetAsync(redis, pid);
        }

        log.LogInformation("2v2 matchmaking: lobby {Lobby}, all players: {Players}", lobbyId, string.Join(", ", all));
        foreach (string pid in all.Where(p => p != id))
        {
            if ((await redis.HashGetAsync($"connections:{pid}", "id")).IsNullOrEmpty)
            {
                log.LogWarning("2v2 matchmaking: Player {Player} has no active connection — cannot queue with disconnected teammate", pid);
                return new MatchmakingAnswer(200, new JsonObject { ["error"] = "Not all party members are connected" });
            }
        }

        foreach (string pid in all.Where(p => p != id))
        {
            var loadout = await HashAsync(redis, $"player:{pid}");
            if (Get(loadout, "character") is { Length: > 0 } character && Get(loadout, "skin") is { Length: > 0 } skin
                && !(await redis.HashGetAsync($"connections:{pid}", "current_ip")).IsNullOrEmpty)
            {
                await PlayedLoadout.RecordAsync(redis, pid, character, skin, Get(loadout, "profileIcon"));
            }
        }

        var connectionInfo = new JsonObject();
        var connections = new JsonObject();
        var players = new JsonObject();
        var recentlyPlayed = new JsonObject();
        foreach (string pid in all)
        {
            connectionInfo[pid] = Region(0.04791003838181496);
            connections[pid] = new JsonArray(Guid.NewGuid().ToString());
            string? theirProfile = (string?)await redis.HashGetAsync($"connections:{pid}", "profile_id");
            players[pid] = TwoVersusTwoPlayer(pid, string.IsNullOrEmpty(theirProfile) ? profile : JsonValue.Create(theirProfile));
            recentlyPlayed[pid] = new JsonArray();
        }

        var answer = Answer(request, id, "2v2-retail", all.Count, 724.7928014055103, "character_TODO_SAME_CHAR_IN_SAME_TEAM", null,
            connectionInfo, connections, players, recentlyPlayed, partyId: lobbyId, profileId: "1252928", idFirst: true);
        return new MatchmakingAnswer(200, answer, () => QueueAsync(redis, id, all, request.Body?["match"], (string)answer["id"]!, "2v2"));
    }

    // The requester's part of both requests: their session, cosmetics and loadout.
    private async Task<(string Id, JsonNode? Profile, MatchmakingAnswer? Failure)> PrepareRequesterAsync(IDatabase redis, PartyRequest request, CancellationToken ct)
    {
        var connection = await HashAsync(redis, $"connections:{request.AccountId}");
        if (Get(connection, "id") is not { Length: > 0 })
        {
            log.LogError("No Redis player connection found for player ID {Player}, this should not happen.", request.AccountId);
        }

        string id = Or(Get(connection, "id"), request.AccountId);
        JsonNode? profile = Get(connection, "profile_id") is { Length: > 0 } stored ? JsonValue.Create(stored) : request.Claims?["profile_id"]?.DeepClone();

        if (!await redis.KeyExistsAsync($"player:{id}:cosmetics"))
        {
            log.LogWarning("No cosmetics found in Redis for AccountId {Player} during matchmaking; caching the equipped ones", request.AccountId);
            await cosmetics.WriteMatchCopyAsync(id, await cosmetics.EquippedAsync(id, ct));
        }

        var loadout = await HashAsync(redis, $"player:{id}");
        if (Get(loadout, "character") is not { Length: > 0 } character || Get(loadout, "skin") is not { Length: > 0 } skin)
        {
            log.LogError("No Redis player loadout found for player ID {Player}, cannot matchmake.", id);
            return (id, profile, new MatchmakingAnswer(500, new JsonObject { ["error"] = "player_loadout_not_found" }));
        }

        await PlayedLoadout.RecordAsync(redis, id, character, skin, Get(loadout, "profileIcon"));
        return (id, profile, null);
    }

    // The Hydra matchmaking request the game is answered with. The 2v2 answer starts with its id; the 1v1 one ends with it.
    private JsonObject Answer(PartyRequest request, string me, string criteria, int playerCount, double rating, string doubleCharacter,
        JsonNode? serverData, JsonObject connectionInfo, JsonObject connections, JsonObject players, JsonObject recentlyPlayed,
        string? partyId, string profileId, bool idFirst)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var data = request.Body?["data"] as JsonObject;
        var answer = new JsonObject();
        string id = ObjectId.GenerateNewId().ToString();
        if (idFirst)
        {
            answer["id"] = id;
        }

        answer["updated_at"] = new JsonObject { ["_hydra_unix_date"] = now };
        answer["requester_account_id"] = me;
        answer["is_concurrent"] = false;
        answer["concurrent_identifier"] = Guid.NewGuid().ToString();
        answer["created_at"] = new JsonObject { ["_hydra_unix_date"] = now };
        answer["data"] = new JsonObject
        {
            ["MultiplayParams"] = data?["MultiplayParams"]?.DeepClone(),
            ["crossplay_buckets"] = new JsonArray("All", "PC"),
            ["version"] = settings.CurrentValue.GameVersion,
            ["matchmaking_rating"] = rating,
            ["player_count"] = playerCount,
            ["double_character_key"] = doubleCharacter,
            ["rp"] = 0,
            ["allowed_buckets"] = new JsonArray("Any"),
            ["allowed_buckets_relaxed"] = new JsonArray("Any"),
        };
        answer["server_data"] = serverData;
        answer["criteria_slug"] = criteria;
        answer["cluster"] = data?["MultiplayParams"]?["MultiplayClusterSlug"]?.DeepClone();
        answer["players_connection_info"] = connectionInfo;
        answer["player_connections"] = connections;
        answer["players"] = players;
        answer["groups"] = new JsonArray(1);
        answer["relationships"] = new JsonArray();
        answer["recently_played"] = recentlyPlayed;
        // undefined in the TS answer when the game sends no match, which its Hydra encoding (the game's) writes as null.
        answer["from_match"] = request.Body?["match"]?.DeepClone();

        answer["reuse_match"] = false;
        answer["party_id"] = partyId;
        answer["state"] = 2;
        answer["user_rule_config"] = new JsonArray();
        answer["game_server"] = new JsonObject
        {
            ["unique_key"] = null,
            ["backend"] = "multiplay",
            ["launch_configs"] = new JsonArray(new JsonObject
            {
                ["profile_id"] = profileId,
                ["fleet_id"] = "6edd4138-20ef-11ec-a2b7-4a5119a45304",
                ["region_id"] = "19c714ff-f21f-11ea-b144-4d87911ee195",
                ["backend"] = "multiplay",
            }),
            ["optional_launch_config_params"] = new JsonObject(),
        };
        answer["server_submitted"] = false;
        if (!idFirst)
        {
            answer["id"] = id;
        }

        return answer;
    }

    private static JsonObject Region(double latency) =>
        new() { ["game_server_region_data"] = new JsonArray(new JsonObject { ["region_id"] = "19c465a7-f21f-11ea-a5e3-0954f48c5682", ["latency"] = latency }) };

    private static JsonObject OneVersusOnePlayer(string id, JsonNode? profile)
    {
        var player = new JsonObject
        {
            ["updated_at"] = null,
            ["account_id"] = id,
            ["created_at"] = null,
            ["last_login"] = null,
            ["last_inbox_read"] = null,
            ["points"] = null,
            ["data"] = new JsonObject(),
            ["server_data"] = s_founders.DeepClone(),
            ["private_data"] = new JsonObject(),
            ["server_owner_data"] = new JsonObject(),
            ["inventory"] = new JsonObject(),
            ["matches"] = new JsonObject(),
            ["cross_match_results"] = new JsonObject(),
            ["notifications"] = new JsonObject(),
            ["aggregates"] = new JsonObject(),
            ["calculations"] = new JsonObject(),
            ["files"] = new JsonArray(),
            ["user_segments"] = new JsonArray(),
            ["random_distribution"] = null,
        };
        // undefined when neither the session nor the token has one.
        if (profile is not null)
        {
            player["id"] = profile.DeepClone();
        }

        return player;
    }

    private static JsonObject TwoVersusTwoPlayer(string id, JsonNode? profile)
    {
        var player = new JsonObject();
        if (profile is not null)
        {
            player["id"] = profile.DeepClone();
        }

        player["updated_at"] = null;
        player["account_id"] = id;
        player["created_at"] = null;
        player["last_login"] = null;
        player["last_inbox_read"] = null;
        player["points"] = null;
        player["data"] = new JsonObject();
        player["cross_match_results"] = new JsonObject();
        player["notifications"] = new JsonObject();
        player["aggregates"] = new JsonObject();
        player["calculations"] = new JsonObject();
        player["files"] = new JsonArray();
        player["random_distribution"] = null;
        return player;
    }

    // queueMatch: the ticket on party:queued; anything failing cancels the request for the leader.
    private async Task QueueAsync(IDatabase redis, string leader, IReadOnlyList<string> players, JsonNode? fromMatch, string requestId, string matchType)
    {
        try
        {
            string ticket = await TicketAsync(redis, leader, players, fromMatch, requestId, matchType, time.GetUtcNow(), CancellationToken.None);
            await redis.PublishAsync(RedisChannel.Literal(QueuedChannel), ticket);
            log.LogInformation("Party ({Party}) matchmakingRequestId({Request}) has been added to {Mode} matchmaking queue. Players ({Players})",
                fromMatch is null ? "undefined" : Js.Stringify(fromMatch).Trim('"'), requestId, matchType, string.Join(",", players));
        }
        catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException)
        {
            log.LogError("Error queueing player: {Error}", e.Message);
            await PublishCancelAsync(redis, [leader], requestId);
            if ((string?)await redis.StringGetAsync($"player_lobby:{leader}") is { Length: > 0 } lobby)
            {
                await redis.KeyDeleteAsync($"party_ready:{lobby}");
            }
        }
    }

    /// <summary>The ticket queueMatch publishes, as JSON.stringify writes it.</summary>
    internal async Task<string> TicketAsync(IDatabase redis, string leader, IReadOnlyList<string> players, JsonNode? fromMatch, string requestId,
        string matchType, DateTimeOffset now, CancellationToken ct)
    {
        var entries = new JsonArray();
        foreach (string pid in players)
        {
            var config = await HashAsync(redis, $"player:{pid}");
            var entry = new JsonObject { ["id"] = pid, ["region"] = "MVSI", ["skill"] = await SkillAsync(pid, Get(config, "character") ?? "", matchType, ct) };
            if (Get(config, "ip") is { } ip)
            {
                entry["ip"] = ip;
            }

            entries.Add(entry);
        }

        var ticket = new JsonObject
        {
            ["created_at"] = now.ToUnixTimeSeconds(),
            ["matchType"] = matchType,
            ["partyLeaderId"] = leader,
            ["matchmakingRequestId"] = requestId,
        };
        // undefined when the game sends no match: JSON leaves the key out.
        if (fromMatch is not null)
        {
            ticket["partyId"] = fromMatch.DeepClone();
        }

        ticket["party_size"] = players.Count;
        ticket["players"] = entries;
        return Js.Stringify(ticket);
    }

    // The player's rating for the character they locked in this mode; 0 for a character not played yet or when the rating
    // cannot be read.
    private async Task<JsonNode> SkillAsync(string playerId, string character, string matchType, CancellationToken ct)
    {
        try
        {
            if (await ratings.GetOrCreateAsync(playerId, "", ct) is not { } rating)
            {
                return 0;
            }

            string field = matchType == "1v1" ? "characters_1v1" : "characters_2v2";
            if (character.Length > 0 && rating.TryGetValue(field, out var chars) && chars is BsonDocument map
                && map.TryGetValue(character, out var entry) && entry is BsonDocument stats && stats.TryGetValue("elo", out var elo) && elo.IsNumeric
                && elo.ToDouble() is var value && value != 0 && !double.IsNaN(value))
            {
                log.LogInformation("Player {Player} using character ELO: {Character} = {Elo}", playerId, character, value);
                return JsonValue.Create(value);
            }

            log.LogInformation("Player {Player} using new character ELO: 0 (char: {Character}, no ranked data)", playerId, character.Length > 0 ? character : "unknown");
            return 0;
        }
        catch (Exception e) when (e is MongoDB.Driver.MongoException or TimeoutException)
        {
            log.LogWarning("Could not fetch ELO for player {Player}, defaulting to 0: {Error}", playerId, e.Message);
            return 0;
        }
    }

    public async Task<JsonObject> CancelAsync(string requestId, PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        string? lobbyId = (string?)await redis.StringGetAsync($"player_lobby:{me}");
        var players = (lobbyId is null ? null : await PlayersOfAsync(redis, lobbyId)) ?? [me];

        await PublishCancelAsync(redis, players, requestId);
        log.LogInformation("Canceling matchmaking {Request} for all players: {Players}", requestId, string.Join(", ", players));
        foreach (string pid in players)
        {
            if ((string?)await redis.StringGetAsync($"player_lobby:{pid}") is { Length: > 0 } theirs)
            {
                await redis.KeyDeleteAsync($"party_ready:{theirs}");
            }
        }

        return new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 };
    }

    private static Task PublishCancelAsync(IDatabase redis, IReadOnlyList<string> players, string requestId) =>
        redis.PublishAsync(RedisChannel.Literal(PartyService.CancelMatchmakingChannel),
            Js.Stringify(new JsonObject { ["playersIds"] = new JsonArray([.. players.Select(p => (JsonNode?)p)]), ["matchmakingId"] = requestId }));

    // redisRemoveExistingTicketsForPlayer: any ticket of the player's leaves the 1v1 and 2v2 lists, with no notice.
    private async Task RemoveTicketsAsync(IDatabase redis, string playerId)
    {
        try
        {
            int removed = 0;
            foreach (string queue in new[] { "1v1", "2v2" })
            {
                foreach (var raw in await redis.ListRangeAsync(queue))
                {
                    JsonObject? ticket;
                    try
                    {
                        ticket = Js.Parse(raw.ToString()) as JsonObject;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue;
                    }

                    if (ticket?["players"] is JsonArray players && players.Any(p => p?["id"] is JsonValue v && v.TryGetValue(out string? s) && s == playerId))
                    {
                        await redis.ListRemoveAsync(queue, raw);
                        removed++;
                    }
                }
            }

            if (removed > 0)
            {
                log.LogInformation("Cleaned up {Count} stale ticket(s) for player {Player} before re-queuing", removed, playerId);
            }
        }
        catch (RedisException e)
        {
            log.LogError("Error cleaning up stale tickets for player {Player}: {Error}", playerId, e.Message);
        }
    }

    // clearStaleRankedSetForPlayer: a player back in the queue is in no ranked set; the set, its check-ins and every
    // member's pointer to it go.
    private async Task EndRankedSetAsync(IDatabase redis, string playerId)
    {
        try
        {
            if ((string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is not { Length: > 0 } setId)
            {
                return;
            }

            var members = new List<string> { playerId };
            if ((string?)await redis.StringGetAsync($"ranked_set:{setId}") is { } raw)
            {
                try
                {
                    if (Js.Parse(raw)?["players"] is JsonArray players)
                    {
                        foreach (var p in players)
                        {
                            if (p?["playerId"] is JsonValue v && v.TryGetValue(out string? pid) && pid.Length > 0 && !members.Contains(pid))
                            {
                                members.Add(pid);
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException e)
                {
                    log.LogError("Error parsing ranked_set:{Set} during stale-set cleanup: {Error}", setId, e.Message);
                }
            }

            foreach (string pid in members)
            {
                await redis.KeyDeleteAsync($"player_ranked_set:{pid}");
            }

            await redis.KeyDeleteAsync($"ranked_set:{setId}");
            await redis.KeyDeleteAsync($"ranked_set_checkins:{setId}");
            log.LogInformation("Cleared stale ranked set {Set} on fresh matchmaking enqueue (player {Player}, {Count} pointer(s) cleared)", setId, playerId, members.Count);
        }
        catch (RedisException e)
        {
            log.LogError("Error clearing stale ranked set for player {Player}: {Error}", playerId, e.Message);
        }
    }

    private static async Task<List<string>?> LobbyPlayersAsync(IDatabase redis, string playerId) =>
        (string?)await redis.StringGetAsync($"player_lobby:{playerId}") is { Length: > 0 } lobbyId ? await PlayersOfAsync(redis, lobbyId) : null;

    // The players of lobby:{id} (redisGetLobbyState); null when there is no such lobby.
    private static async Task<List<string>?> PlayersOfAsync(IDatabase redis, string lobbyId)
    {
        if ((string?)await redis.StringGetAsync($"lobby:{lobbyId}") is not { } raw)
        {
            return null;
        }

        try
        {
            return Js.Parse(raw)?["playerIds"] is JsonArray ids
                ? ids.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : Js.Stringify(n)).ToList()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, string>> HashAsync(IDatabase redis, string key) =>
        (await redis.HashGetAllAsync(key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}

public static class MatchmakingRequestHosting
{
    /// <summary>The matchmaking request and its cancel; reads the lobby settings AddPartyLobbies binds.</summary>
    public static WebApplicationBuilder AddMatchmakingRequests(this WebApplicationBuilder builder)
    {
        builder.AddEloRatings();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMatchmakingRequestService, MatchmakingRequestService>();
        return builder;
    }
}
