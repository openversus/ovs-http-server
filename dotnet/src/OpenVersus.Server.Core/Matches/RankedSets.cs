using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matchmaking;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A ranked best-of-3 set between its games, ported from the TS server's ssc/routes.ts (match_set_checkin,
// match_set_absent, match_set_concede, faceoff_timeout; handleRankedSetCheckin, createNextSetMatch, getPlayerCharacters),
// branch infinity-war. The set itself is made by the matchmaker (game 1: ranked_set:{its match id}, the set id) and
// updated by the TS websocket at each game's end (score, gamesPlayed; match:end there); this is what the players do in
// between, on the post-match screen.
//
//   check-in (Ready; absent = its timer ran out, the same): counted once per player and game. A player of the set whose
//     websocket dropped (ranked_disconnect:{id}) and who is not online any more loses the set (concede). When all are in,
//     either the set is over (2 wins, 3 games, or conceded): rated, then everyone back to the menus; or the next game is
//     made with the same teams.
//   concede: the conceder's team loses the set; rated, then everyone back to the menus.
//   faceoff timeout (an opponent never loaded into the match): the set is dropped, no rating.
// A match the rollback server reported as crashed (match_server_crash) is never rated: the set is dropped at check-in.
// Ratings: SetRatings, when RatedMatches says the set counts. Every request is answered {body: {}} (the endpoints).
//
// Redis, read     player_ranked_set:{player}; ranked_set:{set} {players, mode, gamesPlayed, scores, checkins, conceded,
//                 concedingPlayer}; ranked_set_match:{set}, match_to_set:{game}; match_server_crash:{set},
//                 match_server_crash:{its game};
//                 ranked_disconnect:{player}; online_players; match:{game}, {game} (RatedMatches);
//                 match_characters:{game}, match_characters:{set}, connections:{player} character, username
// Redis, written  ranked_set_lock:{set} (EX 10 s, around everything a set request does); ranked_set_checkins:{set} (SADD,
//                 EX 10 min); ranked_set:{set} (checkins: the members) EX 10 min; set_match_dedup:{set}:{gamesPlayed} NX
//                 EX 30 s; elo_processed_set:{set} NX EX 5 min ("set_over", "concede", "disconnect_concede": the TS
//                 websocket skips a set it finds there); a dropped set: player_ranked_set:{each player} (and
//                 ranked_disconnect:{each player} at a faceoff or crash), ranked_set:{set}, ranked_set_checkins:{set},
//                 ranked_set_match:{set} deleted; a stale ranked_disconnect:{player} (online again) deleted
//   next game     match:{game} (one ticket of every player: skill 0, region local, partyId the game; no isPasswordMatch,
//                 so it is rated) EX 20 min; {game} (the notification: the set's players as they were, a map, p2p) EX 20
//                 min; ranked_set:{set} with checkins [] and ranked_set_checkins:{set} deleted, player_ranked_set:{each}
//                 and match_to_set:{game} (the TS websocket's fallback) EX 20 min; ranked_set_match:{set} EX 30 min
// Published       ranked_set:checkin {playerIds, checkedInPlayer, checkins, totalPlayers, setId}; ranked_set:leaver
//                 {playerIds, leaverPlayerId, matchId: the set} (the TS websocket: MatchSetLeaverNotification, then the
//                 empty config that sends the game back to its menus; 500 ms after the answer when a set ends at
//                 check-in); ranked_set:fullrankupdate {playerIds} after a rating; match:notifications (the next game)
// Mongo, written  eloratings, playerstats (SetRatings)
//
// The next game's rollback port is IMatchLauncher's (fixed servers: a random one of theirs; on demand: the next port,
// deployed unless the game runs P2P), as the matchmaker's; its p2p is P2P.Mark (Rollback:P2P). The teams and player
// indexes are the set's players unchanged, as there.
//
// Unlike there:
//   - every request on a set waits for the set's lock (up to 12 s) and counts check-ins under it. TS took the count
//     before the lock and dropped a request that found the lock taken: two check-ins at the same moment (both timers
//     running out) could each see one check-in, and the set never went on. A concede that found it taken was dropped.
//   - the set's current game is kept (ranked_set_match:{set}, EX 30 min: longer than a whole set; the set id is game 1). A
//     check-in for another game (ContainerMatchId; the game sends the game just played, and an absent when its timer
//     runs out even after a check-in) is ignored: a late absent from the previous game no longer counts toward the
//     next. With no ContainerMatchId, or no current game known (game 1, or the key gone), every check-in counts, as there.
//   - match_server_crash is read for the current game too (it is written under the game's id; TS read only the set's,
//     game 1's, so a crash in game 2 or 3 was rated).
//   - a crash drops the set for every player of it, and all of them are sent back to the menus (TS: only the player
//     checking in; the others' check-ins then found no set and got no answer).
//   - characters (for the ratings) come from the current game's match_characters first: a player keeps their fighter
//     for the whole set, so it is the same one as game 1's, and the newest copy.
//   - the next game's set keys (ranked_set, player_ranked_set, match_to_set) live 20 min, as the match's own (TS: 10):
//     the TS websocket refreshes them only at the game's end, and a game can take ~9.5 min from its creation (perks
//     30 s, connecting up to 45 s, up to 7.5 min of play, loading and the play after the end).
//   - nothing is written for a player whose set is gone (TS added the check-in to a key nobody read again).
//   - a set is rated only when RatedMatches says it counts (MIGRATION-BRIDGES.md 6).

public interface IRankedSets
{
    /// <summary>match_set_checkin (and match_set_absent): <paramref name="playerId"/> is ready for the next game after
    /// <paramref name="containerMatchId"/> (null when the game sent none).</summary>
    Task CheckinAsync(string playerId, string? containerMatchId);

    /// <summary>match_set_concede: <paramref name="playerId"/>'s team gives up the set.</summary>
    Task ConcedeAsync(string playerId);

    /// <summary>faceoff_timeout: an opponent of <paramref name="playerId"/> never loaded into the match; the set is dropped.</summary>
    Task FaceoffTimeoutAsync(string playerId);

    /// <summary>submit_end_of_match_stats: game <paramref name="matchId"/> was won by team <paramref name="winner"/> (as
    /// decided from every report so far, MatchWinner); the score of the set of the first of <paramref name="playerIds"/>
    /// (the reporter, then the match's players) that is in one follows it.</summary>
    Task RecordWinnerAsync(IReadOnlyList<string> playerIds, string matchId, int winner);
}

internal sealed class RankedSets(IServiceProvider services, IMatchLauncher launcher, ISetRatings ratings, IOptionsMonitor<RollbackSettings> rollback,
    TimeProvider time, ILogger<RankedSets> log) : IRankedSets
{
    public const string CheckinChannel = "ranked_set:checkin";
    public const string LeaverChannel = "ranked_set:leaver";
    public const string FullRankUpdateChannel = "ranked_set:fullrankupdate";
    private static readonly TimeSpan s_setTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_matchTtl = TimeSpan.FromMinutes(20);
    // Longer than a whole set: three games of up to 7.5 min, each with its perk screen (30 s), connecting to the rollback
    // server (up to 45 s), loading and the play after the end, and the post-match screen (30 s): about 25 min.
    private static readonly TimeSpan s_currentGameTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_lockTtl = TimeSpan.FromSeconds(10);
    internal static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(12);
    internal static TimeSpan LeaverDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public async Task CheckinAsync(string playerId, string? containerMatchId)
    {
        if (Redis() is not { } redis)
        {
            return;
        }

        if ((string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is not { Length: > 0 } setId)
        {
            log.LogWarning("No active ranked set for player {Player}", playerId);
            return;
        }

        await using var held = await LockAsync(redis, setId);
        if (held is null)
        {
            log.LogError("Set {Set} stayed locked for {Wait} s: check-in from {Player} dropped", setId, LockWait.TotalSeconds, playerId);
            return;
        }

        if (await SetAsync(redis, setId) is not { } set)
        {
            log.LogWarning("Ranked set {Set} not found in Redis", setId);
            return;
        }

        // Only a known current game makes a check-in for another one stale. Without it (game 1, or the pointer gone) every
        // check-in counts, as there, and the game it names is the current one when it is this set's (match_to_set).
        string? known = await KnownGameAsync(redis, setId);
        if (known is not null && containerMatchId is { Length: > 0 } && containerMatchId != known)
        {
            log.LogInformation("Check-in from {Player} for match {Match} ignored: set {Set} is at match {Current}", playerId, containerMatchId, setId, known);
            return;
        }

        string current = known ?? (containerMatchId is { Length: > 0 } && containerMatchId != setId
            && (string?)await redis.StringGetAsync($"match_to_set:{containerMatchId}") == setId ? containerMatchId : setId);

        string checkinKey = $"ranked_set_checkins:{setId}";
        bool added = await redis.SetAddAsync(checkinKey, playerId);
        await redis.KeyExpireAsync(checkinKey, s_setTtl);
        long checkinCount = await redis.SetLengthAsync(checkinKey);
        var all = PlayerIds(set);

        if (!added)
        {
            log.LogInformation("Player {Player} already checked in for set {Set}, checking for disconnected opponents", playerId, setId);
        }
        else
        {
            set["checkins"] = new JsonArray([.. (await redis.SetMembersAsync(checkinKey)).Select(m => (JsonNode)m.ToString())]);
            await redis.StringSetAsync($"ranked_set:{setId}", Js.Stringify(set), s_setTtl);
            log.LogInformation("Set {Set} check-in: {Count}/{Total}", setId, checkinCount, all.Count);
        }

        // A crash (everyone left with no result, or the rollback server failed): not fair to rate anyone.
        if (await CrashedAsync(redis, setId, current) is { } crash)
        {
            log.LogInformation("Set {Set} flagged as crash ({Flag}) — skipping auto-concede, cleaning up", setId, crash);
            await DropAsync(redis, setId, all, disconnectFlags: true);
            await PublishAsync(redis, LeaverChannel, Leaver(all, playerId, setId));
            return;
        }

        // A player whose websocket dropped and who is still offline concedes the set; one online again left a stale flag.
        foreach (string other in all.Where(id => id != playerId))
        {
            if (!(await redis.StringGetAsync($"ranked_disconnect:{other}")).HasValue)
            {
                continue;
            }

            if (await redis.SetContainsAsync("online_players", other))
            {
                log.LogInformation("Stale ranked_disconnect flag for {Player} (online now), cleaning up", other);
                await redis.KeyDeleteAsync($"ranked_disconnect:{other}");
                continue;
            }

            log.LogInformation("Player {Player} disconnected and offline — auto-conceding set {Set}", other, setId);
            await redis.KeyDeleteAsync($"ranked_disconnect:{other}");
            await ConcedeAndRateAsync(redis, set, setId, current, other, "disconnect_concede");
            await DropAsync(redis, setId, all, disconnectFlags: false);
            await PublishAsync(redis, LeaverChannel, Leaver(all, other, setId));
            return;
        }

        await PublishAsync(redis, CheckinChannel, new JsonObject
        {
            ["playerIds"] = Strings(all),
            ["checkedInPlayer"] = playerId,
            ["checkins"] = set["checkins"]?.DeepClone(),
            ["totalPlayers"] = all.Count,
            ["setId"] = setId,
        });

        if (checkinCount < all.Count)
        {
            return;
        }

        string gamesPlayed = set["gamesPlayed"] is { } g ? Js.Stringify(g) : "undefined";
        if (!await redis.StringSetAsync($"set_match_dedup:{setId}:{gamesPlayed}", "1", TimeSpan.FromSeconds(30), When.NotExists))
        {
            log.LogInformation("Match already created for set {Set} game {Games}, skipping", setId, gamesPlayed);
            return;
        }

        var (team0Wins, team1Wins) = Scores(set);
        bool conceded = Truthy(set["conceded"]);
        if (conceded || team0Wins >= 2 || team1Wins >= 2 || Number(set["gamesPlayed"]) >= 3)
        {
            log.LogInformation("Set {Set} is over ({Reason}), processing ELO and ending set", setId, conceded ? "concede" : $"score {team0Wins}-{team1Wins}");
            if (await redis.StringSetAsync($"elo_processed_set:{setId}", "set_over", TimeSpan.FromMinutes(5), When.NotExists))
            {
                await RateAndAnnounceAsync(redis, set, setId, current, team0Wins > team1Wins ? 0 : 1, conceded, "set");
            }
            else
            {
                log.LogInformation("ELO already processed for set {Set}, skipping", setId);
            }

            await DropAsync(redis, setId, all, disconnectFlags: false);
            // After the answer, as there.
            string leaver = set["concedingPlayer"] is JsonValue c && c.TryGetValue(out string? conceder) && conceder.Length > 0 ? conceder : all.FirstOrDefault() ?? "";
            _ = Task.Run(async () =>
            {
                await Task.Delay(LeaverDelay, time);
                await PublishAsync(redis, LeaverChannel, Leaver(all, leaver, setId));
                log.LogInformation("Sent MatchSetLeaverNotification for set {Set}", setId);
            });
            return;
        }

        log.LogInformation("All players checked in for set {Set}, creating next match (game {Game}/3)", setId, Number(set["gamesPlayed"]) + 1);
        await NextGameAsync(redis, setId, set, all);
    }

    public async Task ConcedeAsync(string playerId)
    {
        if (Redis() is not { } redis || (string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is not { Length: > 0 } setId)
        {
            return;
        }

        await using var held = await LockAsync(redis, setId);
        if (held is null)
        {
            log.LogError("Set {Set} stayed locked for {Wait} s: concede from {Player} dropped", setId, LockWait.TotalSeconds, playerId);
            return;
        }

        if (await SetAsync(redis, setId) is not { } set)
        {
            return;
        }

        var all = PlayerIds(set);
        await ConcedeAndRateAsync(redis, set, setId, await CurrentGameAsync(redis, setId), playerId, "concede");
        await DropAsync(redis, setId, all, disconnectFlags: false);
        await PublishAsync(redis, LeaverChannel, Leaver(all, playerId, setId));
        log.LogInformation("Player {Player} conceded set {Set}, sending MatchSetLeaverNotification", playerId, setId);
    }

    public async Task FaceoffTimeoutAsync(string playerId)
    {
        if (Redis() is not { } redis || (string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is not { Length: > 0 } setId
            || await SetAsync(redis, setId) is not { } set)
        {
            return;
        }

        log.LogInformation("faceoff_timeout cleaning up ranked set {Set}", setId);
        await DropAsync(redis, setId, PlayerIds(set), disconnectFlags: true);
    }

    // The set score, as the TS submit_end_of_match_stats kept it: a game's win counted once (ranked_set_score:{set}:{game}),
    // here holding the team it went to, so that a later report that changes the decided winner moves the point. No set for
    // the player (the matchmaker made none): the winner waits for the TS websocket's match end
    // (ranked_set_pending_winner:{game}, EX 2 min), which opens a set from it.
    public async Task RecordWinnerAsync(IReadOnlyList<string> playerIds, string matchId, int winner)
    {
        if (winner is not (0 or 1) || playerIds.Count == 0 || Redis() is not { } redis)
        {
            return;
        }

        // The reporter's set, else another player's of the match (a report that decides may come from a spectator).
        string playerId = playerIds[0];
        string? setId = null;
        foreach (string id in playerIds)
        {
            if ((string?)await redis.StringGetAsync($"player_ranked_set:{id}") is { Length: > 0 } pointer)
            {
                (playerId, setId) = (id, pointer);
                break;
            }
        }

        if (setId is null)
        {
            await redis.StringSetAsync($"ranked_set_pending_winner:{matchId}", winner.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromMinutes(2));
            log.LogWarning("FALLBACK: Stored pending_winner (team {Winner}) for match {Match} — player {Player} has no player_ranked_set. Pre-creation likely failed.", winner, matchId, playerId);
            return;
        }

        await using var held = await LockAsync(redis, setId);
        if (held is null)
        {
            log.LogError("Set {Set} stayed locked for {Wait} s: the winner of match {Match} was not counted", setId, LockWait.TotalSeconds, matchId);
            return;
        }

        if (await SetAsync(redis, setId) is not { } set)
        {
            log.LogWarning("FALLBACK: player_ranked_set:{Player}={Set} but ranked_set:{Set} is missing. Score for match {Match} (winner=team {Winner}) cannot be incremented.",
                playerId, setId, setId, matchId, winner);
            return;
        }

        string scoreKey = $"ranked_set_score:{setId}:{matchId}";
        int? counted = (string?)await redis.StringGetAsync(scoreKey) is { } was && int.TryParse(was, out int team) && team is 0 or 1 ? team : null;
        if (counted == winner)
        {
            return;
        }

        var scores = set["scores"] as JsonArray is { Count: >= 2 } stored ? stored : new JsonArray(0, 0);
        if (counted is { } previous)
        {
            scores[previous] = Number(scores[previous]) - 1;
            log.LogWarning("Match {Match} of set {Set}: the reports now say team {Winner} won, not team {Previous}; the point moves", matchId, setId, winner, previous);
        }

        scores[winner] = Number(scores[winner]) + 1;
        set["scores"] = scores.DeepClone();
        await redis.StringSetAsync($"ranked_set:{setId}", Js.Stringify(set), s_setTtl);
        await redis.StringSetAsync(scoreKey, winner.ToString(System.Globalization.CultureInfo.InvariantCulture), s_setTtl);
        log.LogInformation("Ranked set {Set} scores updated: {Team0}-{Team1}", setId, Js.Stringify(scores[0]), Js.Stringify(scores[1]));
    }

    // ── Ratings ─────────────────────────────────────────────────────────────────────────────────────────────────────

    // The conceder's team loses the set (rated once per set: elo_processed_set).
    private async Task ConcedeAndRateAsync(IDatabase redis, JsonObject set, string setId, string current, string conceder, string reason)
    {
        if (!await redis.StringSetAsync($"elo_processed_set:{setId}", reason, TimeSpan.FromMinutes(5), When.NotExists))
        {
            log.LogInformation("ELO already processed for set {Set}, skipping {Reason}", setId, reason);
            return;
        }

        if ((set["players"] as JsonArray ?? []).FirstOrDefault(p => Text(p?["playerId"]) == conceder)?["teamIndex"] is not JsonValue team)
        {
            return;
        }

        await RateAndAnnounceAsync(redis, set, setId, current, Number(team) == 0 ? 1 : 0, isConcede: true, reason);
    }

    // Rates the set for winnerTeam, then ranked_set:fullrankupdate (the TS websocket sends each player their ranks). A
    // failure is logged and announces nothing, as there.
    private async Task RateAndAnnounceAsync(IDatabase redis, JsonObject set, string setId, string current, int winnerTeam, bool isConcede, string reason)
    {
        try
        {
            var players = set["players"] as JsonArray ?? [];
            string mode = Text(set["mode"]) ?? "";
            if (await CrashedAsync(redis, setId, current) is not null)
            {
                log.LogInformation("Skipping ELO for set {Set} — rollback server crashed (match_server_crash flag set)", setId);
            }
            else if (RatedMatches.WhyNotRated(mode, players, await JsonAsync(redis, $"match:{current}") ?? await JsonAsync(redis, $"match:{setId}"),
                         await JsonAsync(redis, current) ?? await JsonAsync(redis, setId)) is { } why)
            {
                log.LogWarning("Set {Set} not rated: {Why}", setId, why);
            }
            else
            {
                var (team0Wins, team1Wins) = Scores(set);
                var team0 = players.Where(p => Number(p?["teamIndex"]) == 0).Select(p => Text(p?["playerId"]) ?? "").ToList();
                var team1 = players.Where(p => Number(p?["teamIndex"]) == 1).Select(p => Text(p?["playerId"]) ?? "").ToList();
                var winners = winnerTeam == 0 ? team0 : team1;
                var losers = winnerTeam == 0 ? team1 : team0;
                await ratings.RateAsync(new SetOutcome(winners, losers, mode, team0Wins, team1Wins, winnerTeam, isConcede,
                    await CharactersAsync(redis, [.. winners, .. losers], setId, current), setId), CancellationToken.None);
            }

            await PublishAsync(redis, FullRankUpdateChannel, new JsonObject { ["playerIds"] = Strings(PlayerIds(set)) });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogError("Error processing {Reason} ELO for set {Set}: {Error}", reason, setId, e.Message);
        }
    }

    // Each player's fighter: the current game's match_characters, then game 1's (the same fighter: it is locked for the
    // set), then the player's connection.
    private static async Task<Dictionary<string, string>> CharactersAsync(IDatabase redis, IEnumerable<string> playerIds, string setId, string current)
    {
        var characters = new Dictionary<string, string>();
        foreach (string game in current == setId ? [current] : new[] { current, setId })
        {
            if (await JsonAsync(redis, $"match_characters:{game}") is { } stored)
            {
                foreach (string id in playerIds.Where(id => !characters.ContainsKey(id)))
                {
                    if (Text(stored[id]) is { Length: > 0 } character)
                    {
                        characters[id] = character;
                    }
                }
            }
        }

        foreach (string id in playerIds.Where(id => !characters.ContainsKey(id)))
        {
            if ((string?)await redis.HashGetAsync($"connections:{id}", "character") is { Length: > 0 } character)
            {
                characters[id] = character;
            }
        }

        return characters;
    }

    // ── The next game ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task NextGameAsync(IDatabase redis, string setId, JsonObject set, List<string> all)
    {
        string mode = Text(set["mode"]) ?? "";
        if (await launcher.RollbackPortAsync(redis) is not { } port)
        {
            log.LogError("No rollback port for the next game of set {Set}: it cannot go on", setId);
            return;
        }

        string matchId = ObjectId.GenerateNewId().ToString();
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ticket = new JsonObject
        {
            ["party_size"] = all.Count,
            ["players"] = new JsonArray([.. all.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["skill"] = 0, ["region"] = "local", ["partyId"] = matchId })]),
            ["created_at"] = now,
            ["partyId"] = matchId,
            ["matchmakingRequestId"] = matchId,
        };
        await redis.StringSetAsync($"match:{matchId}", Js.Stringify(new JsonObject
        {
            ["matchId"] = matchId,
            ["resultId"] = ObjectId.GenerateNewId().ToString(),
            ["tickets"] = new JsonArray(ticket),
            ["status"] = "pending",
            ["createdAt"] = now,
            ["matchType"] = set["mode"]?.DeepClone(),
            ["totalPlayers"] = all.Count,
            ["rollbackPort"] = port,
        }), s_matchTtl);

        string map = MatchmakingMaps.Pick(mode, matchId, log);
        var notification = new JsonObject
        {
            ["players"] = set["players"]?.DeepClone(),
            ["matchId"] = matchId,
            ["matchKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["map"] = map,
            ["mode"] = set["mode"]?.DeepClone(),
            ["rollbackPort"] = port,
        };
        bool p2p = P2P.Mark(notification, rollback.CurrentValue.P2P);
        if (!p2p)
        {
            launcher.DeployIfOnDemand(port, matchId);
        }

        string json = Js.Stringify(notification);
        await redis.StringSetAsync(matchId, json, s_matchTtl);
        await redis.PublishAsync(RedisChannel.Literal(MatchLauncher.NotificationChannel), json);

        set["checkins"] = new JsonArray();
        await redis.KeyDeleteAsync($"ranked_set_checkins:{setId}");
        // The set's keys must last until the game's end, when the TS websocket writes them again: as long as the match's.
        await redis.StringSetAsync($"ranked_set:{setId}", Js.Stringify(set), s_matchTtl);
        foreach (string id in all)
        {
            await redis.StringSetAsync($"player_ranked_set:{id}", setId, s_matchTtl);
        }

        // The TS websocket's way back to the set when a player's player_ranked_set is gone at the game's end.
        await redis.StringSetAsync($"match_to_set:{matchId}", setId, s_matchTtl);
        // Written again for each game, and kept longer than a whole set: the TS websocket refreshes the set's keys at a
        // game's end and knows nothing of this one.
        await redis.StringSetAsync($"ranked_set_match:{setId}", matchId, s_currentGameTtl);
        log.LogInformation("Created set match {Match} (game {Game}/3) on map {Map}, rollback port {Port}{P2P}", matchId, Number(set["gamesPlayed"]) + 1, map, port,
            p2p ? " (P2P: the players connect to their own nodes; a relay only if no direct path opens)" : "");
    }

    // ── Redis ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private IDatabase? Redis()
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
        {
            return redis;
        }

        log.LogError("Ranked set request not handled: this service has no Redis (REDIS)");
        return null;
    }

    // The set's lock (ranked_set_lock:{set}, as the TS server named it), waited for; null when it stayed taken.
    private async Task<SetLock?> LockAsync(IDatabase redis, string setId)
    {
        string key = $"ranked_set_lock:{setId}";
        string token = Guid.NewGuid().ToString("N");
        var deadline = time.GetUtcNow() + LockWait;
        while (!await redis.LockTakeAsync(key, token, s_lockTtl))
        {
            if (time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), time);
        }

        return new SetLock(redis, key, token);
    }

    private sealed class SetLock(IDatabase redis, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await redis.LockReleaseAsync(key, token);
    }

    // The set's current game: the last one made here, else game 1 (the set's id).
    private static async Task<string> CurrentGameAsync(IDatabase redis, string setId) => await KnownGameAsync(redis, setId) ?? setId;

    // The last game made here for the set (ranked_set_match:{set}); null for game 1, or once the key is gone.
    private static async Task<string?> KnownGameAsync(IDatabase redis, string setId) =>
        (string?)await redis.StringGetAsync($"ranked_set_match:{setId}") is { Length: > 0 } game ? game : null;

    private static async Task<string?> CrashedAsync(IDatabase redis, string setId, string current)
    {
        foreach (string game in current == setId ? [setId] : new[] { setId, current })
        {
            if ((string?)await redis.StringGetAsync($"match_server_crash:{game}") is { } flag)
            {
                return flag;
            }
        }

        return null;
    }

    // Drops the set: every player's pointer (and disconnect flag), the state, the check-ins, the current game.
    private static async Task DropAsync(IDatabase redis, string setId, List<string> all, bool disconnectFlags)
    {
        foreach (string id in all)
        {
            await redis.KeyDeleteAsync($"player_ranked_set:{id}");
            if (disconnectFlags)
            {
                await redis.KeyDeleteAsync($"ranked_disconnect:{id}");
            }
        }

        await redis.KeyDeleteAsync($"ranked_set:{setId}");
        await redis.KeyDeleteAsync($"ranked_set_checkins:{setId}");
        await redis.KeyDeleteAsync($"ranked_set_match:{setId}");
    }

    private static Task PublishAsync(IDatabase redis, string channel, JsonObject message) =>
        redis.PublishAsync(RedisChannel.Literal(channel), Js.Stringify(message));

    private static JsonObject Leaver(List<string> all, string leaver, string setId) => new()
    {
        ["playerIds"] = Strings(all),
        ["leaverPlayerId"] = leaver,
        ["matchId"] = setId,
    };

    private static async Task<JsonObject?> SetAsync(IDatabase redis, string setId) => await JsonAsync(redis, $"ranked_set:{setId}");

    private static async Task<JsonObject?> JsonAsync(IDatabase redis, string key)
    {
        if ((string?)await redis.StringGetAsync(key) is not { } raw)
        {
            return null;
        }

        try
        {
            return Js.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ── Set state, as JavaScript reads it ──────────────────────────────────────────────────────────────────────────

    // The set's players, each once, in order.
    private static List<string> PlayerIds(JsonObject set) =>
        [.. (set["players"] as JsonArray ?? []).Select(p => Text(p?["playerId"])).OfType<string>().Distinct()];

    private static (int Team0, int Team1) Scores(JsonObject set) =>
        set["scores"] is JsonArray { Count: >= 2 } scores ? ((int)Number(scores[0]), (int)Number(scores[1])) : (0, 0);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double Number(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : double.NaN;

    private static bool Truthy(JsonNode? value) => value is JsonValue v && v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.Number => Number(v) is var d && d != 0 && !double.IsNaN(d),
        System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length > 0,
        _ => false,
    };
}

public static class RankedSetsHosting
{
    /// <summary>The ranked set routes' work (and the match launcher and ratings it needs).</summary>
    public static WebApplicationBuilder AddRankedSets(this WebApplicationBuilder builder)
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(IMatchLauncher)))
        {
            builder.AddMatchLauncher();
        }

        builder.AddSetRatings();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IRankedSets, RankedSets>();
        return builder;
    }
}
