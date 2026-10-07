using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A ranked best-of-3 set between its games, ported from the TS server's ssc/routes.ts (match_set_checkin,
// match_set_absent, match_set_concede, faceoff_timeout; handleRankedSetCheckin, createNextSetMatch, getPlayerCharacters),
// branch infinity-war. The set itself is made by the matchmaker (game 1: ranked_set:{its match id}, the set id) and
// updated by the TS websocket at each game's end (score, gamesPlayed; match:end there); this is what the players do in
// between, on the post-match screen.
//
//   check-in (Ready; absent = its timer ran out, the same): counted once per player and game. A player of the set whose
//     websocket dropped (ranked_disconnect:{id}, naming this set) and who is not online any more loses the set (concede).
//   a game's end (MatchEnd): the score as counted, then the set goes on (its state for the check-ins, conceded when a
//     player's flag names it), or is over (rated once, dropped). When all are in,
//     either the set is over (2 wins, 3 games, or conceded): rated, then everyone back to the menus; or the next game is
//     made with the same teams.
//   concede: the conceder's team loses the set; rated, then everyone back to the menus.
//   faceoff timeout (an opponent never loaded into the match): the set is dropped, no rating.
// A match the rollback server reported as crashed (match_server_crash) is never rated: the set is dropped at check-in. So
// is a set whose player's gateway node died between its games (ranked_set_crashed:{set}, MatchStatusEvents), but for its
// rating: it is read at check-in only, so a set that ends on its results is rated.
// Ratings: SetRatings, when RatedMatches says the set counts. Every request is answered {body: {}} (the endpoints).
//
// Redis, read     player_ranked_set:{player}; ranked_set:{set} {players, mode, gamesPlayed, scores, checkins, conceded,
//                 concedingPlayer}; ranked_set_match:{set}, match_to_set:{game}; match_server_crash:{set},
//                 match_server_crash:{its game}; ranked_set_crashed:{set} (at check-in);
//                 ranked_disconnect:{player}; online_players; match:{game}, {game} (RatedMatches);
//                 match_characters:{game}, match_characters:{set}, connections:{player} character, username
// Redis, written  ranked_set_lock:{set} (EX 10 s, around everything a set request does); ranked_set_checkins:{set} (SADD,
//                 EX 10 min); ranked_set:{set} (checkins: the members) EX 10 min; set_match_dedup:{set}:{gamesPlayed} NX
//                 EX 30 s; elo_processed_set:{set} NX EX 5 min ("set_over", "concede", "disconnect_concede": the TS
//                 websocket skips a set it finds there); a dropped set: player_ranked_set:{each player} (and
//                 ranked_disconnect:{each player} at a faceoff or crash), ranked_set:{set}, ranked_set_checkins:{set},
//                 ranked_set_crashed:{set},
//                 ranked_set_match:{set} deleted; a stale ranked_disconnect:{player} (online again) deleted
//   next game     match:{game} (one ticket of every player: skill 0, region local, partyId the game; no isPasswordMatch,
//                 so it is rated) EX 20 min; {game} (the notification: the set's players as they were, a map, p2p) EX 20
//                 min; ranked_set:{set} with checkins [] and ranked_set_checkins:{set} deleted, player_ranked_set:{each}
//                 and match_to_set:{game} (the TS websocket's fallback) EX 20 min; ranked_set_match:{set} EX 30 min
//                 realtime:due (DelayedMessages: a leaver's empty config, and a set over at check-in's leaver)
// Sent (ws:send)  what the TS websocket built from the ranked_set channels, to every player of the set:
//                 MatchSetCheckinNotification {CheckedInAccountId, CheckedInCount (the set's checkins), TotalPlayers} at
//                 each counted check-in; MatchSetLeaverNotification {AccountId: the leaver, MatchId: the set}, then 500
//                 ms later the empty config that sends the game back to its menus (a set over at check-in: both 500 ms
//                 after the answer, as there); FullRankUpdate (FullRankUpdateVariant.SetResult) after a rating, to every
//                 player of the set but bots, connected or not (FullRankUpdate.SendAsync)
// Announced       the next game (MatchLaunches: match:launched)
// Mongo, written  eloratings, playerstats (SetRatings); eloratings for a player with none (FullRankUpdate)
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
//   - a ranked_disconnect flag counts only for the set it names (decided 2026-10-05): any other is stale (TS took any
//     flag, "1" after any started match included, so a custom game's dodge conceded the player's next set).
//   - the messages a moment later (a leaver's empty config, a set over at check-in's leaver) are kept in Redis
//     (DelayedMessages), not in a timer of one process: a restart still sends them. The FullRankUpdate goes out right
//     after the rating, before the leaver; TS's order between the two was a race (its rank handler waited on Mongo).

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

    /// <summary>
    /// A game's end (MatchEnd), for the set part of it: <paramref name="playerIds"/> (match:end's, in order) of
    /// <paramref name="matchId"/>, whose config <paramref name="config"/> is a set game when <paramref name="counts"/>
    /// (RatedMatches). Returns what happened, for the messages.
    /// </summary>
    Task<GameEndResult> GameEndedAsync(string matchId, IReadOnlyList<string> playerIds, JsonObject? config, bool counts);
}

/// <summary>What a game's end did to its set.</summary>
public enum GameEnd
{
    /// <summary>Not a set game (it does not count): nothing done.</summary>
    NotASet,

    /// <summary>The match crashed (match_server_crash): its players' set pointers and dodge flags dropped.</summary>
    Crashed,

    /// <summary>A set game with no set and no recorded winner: pointers and flags dropped, no set made.</summary>
    Orphan,

    /// <summary>The set was already resolved (elo_processed_set): nothing changed.</summary>
    Resolved,

    /// <summary>The set goes on: its state written for the check-ins.</summary>
    Continues,

    /// <summary>The set is over: rated (once), and dropped.</summary>
    Over,
}

/// <summary><paramref name="Kind"/>; for Over, the set's players (each once) and the rated ones (winners, then losers).</summary>
public sealed record GameEndResult(GameEnd Kind, IReadOnlyList<string> SetPlayerIds, IReadOnlyList<string> RatedPlayerIds)
{
    public static GameEndResult Of(GameEnd kind) => new(kind, [], []);
}

internal sealed class RankedSets(IServiceProvider services, IMatchLauncher launcher, ISetRatings ratings, EloRatings eloRatings,
    IOptionsMonitor<RollbackSettings> rollback, IOptionsMonitor<SeasonSettings> season, TimeProvider time, ILogger<RankedSets> log) : IRankedSets
{
    private static readonly TimeSpan s_setTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_matchTtl = TimeSpan.FromMinutes(20);
    // Longer than a whole set: three games of up to 7.5 min, each with its perk screen (30 s), connecting to the rollback
    // server (up to 45 s), loading and the play after the end, and the post-match screen (30 s): about 25 min.
    private static readonly TimeSpan s_currentGameTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_lockTtl = TimeSpan.FromSeconds(10);
    internal static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(12);
    internal static TimeSpan LeaverDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_emptyConfigDelay = TimeSpan.FromMilliseconds(500);

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

        // A crash (everyone left with no result, the rollback server failed, or a player's gateway node died between the
        // games): not fair to rate anyone.
        if ((await CrashedAsync(redis, setId, current) ?? (string?)await redis.StringGetAsync($"ranked_set_crashed:{setId}")) is { } crash)
        {
            log.LogInformation("Set {Set} flagged as crash ({Flag}) — skipping auto-concede, cleaning up", setId, crash);
            await DropAsync(redis, setId, all, disconnectFlags: true);
            await LeaverAsync(redis, all, playerId, setId, TimeSpan.Zero);
            return;
        }

        // A player whose websocket dropped and who is still offline concedes the set; one online again left a stale flag,
        // and so did one whose flag names another set (decided 2026-10-05: a flag counts for the set it names).
        foreach (string other in all.Where(id => id != playerId))
        {
            var flag = await redis.StringGetAsync($"ranked_disconnect:{other}");
            if (!flag.HasValue)
            {
                continue;
            }

            if ((string?)flag != setId)
            {
                log.LogInformation("Stale ranked_disconnect flag for {Player} (set {Flag}, not {Set}), cleaning up", other, (string?)flag, setId);
                await redis.KeyDeleteAsync($"ranked_disconnect:{other}");
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
            await LeaverAsync(redis, all, other, setId, TimeSpan.Zero);
            return;
        }

        // The count is the set's checkins as just written (as stored when the player was already in), as the TS websocket
        // read it from the published set; a set with no such list sent nothing there (it threw).
        if (set["checkins"] is JsonArray checkins)
        {
            foreach (string id in all)
            {
                await ProfileNotifications.SendAsync(redis, id, new JsonObject
                {
                    ["CheckedInAccountId"] = playerId,
                    ["CheckedInCount"] = checkins.Count,
                    ["TotalPlayers"] = all.Count,
                    ["template_id"] = "MatchSetCheckinNotification",
                });
            }
        }
        else
        {
            log.LogError("Set {Set} has no checkins list: check-in from {Player} not announced", setId, playerId);
        }

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
            await LeaverAsync(redis, all, leaver, setId, LeaverDelay);
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
        await LeaverAsync(redis, all, playerId, setId, TimeSpan.Zero);
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

    // ── A game's end ───────────────────────────────────────────────────────────────────────────────────────────────

    // As the TS websocket's handleOnMatchEnd from its common reads to the set's update (websocket.ts), with three changes
    // (decided 2026-10-05): a set game is one RatedMatches counts (TS: any config without isCustomGame, so a rift was a
    // set of its own); a dodge flag counts only when it names this set (TS: any flag, from any match, up to 10 min old);
    // and the update holds the set's lock (TS read and wrote the set while a winner could be being counted).
    public async Task<GameEndResult> GameEndedAsync(string matchId, IReadOnlyList<string> playerIds, JsonObject? config, bool counts)
    {
        if (Redis() is not { } redis)
        {
            return GameEndResult.Of(GameEnd.NotASet);
        }

        // The set of the first player, else the game's (match_to_set, written with each game), given back to all of them.
        string? existingSetId = playerIds.Count > 0 && (string?)await redis.StringGetAsync($"player_ranked_set:{playerIds[0]}") is { Length: > 0 } own ? own : null;
        if (existingSetId is null && (string?)await redis.StringGetAsync($"match_to_set:{matchId}") is { Length: > 0 } fallback)
        {
            existingSetId = fallback;
            log.LogInformation("Recovered setId {Set} for match {Match} via match_to_set fallback", fallback, matchId);
            foreach (string id in playerIds)
            {
                await redis.StringSetAsync($"player_ranked_set:{id}", fallback, s_setTtl);
            }
        }

        if ((string?)await redis.StringGetAsync($"match_server_crash:{matchId}") is { } crash)
        {
            log.LogInformation("Match {Match} flagged as crash ({Flag}) — no set update, cleaning up stale state", matchId, crash);
            foreach (string id in playerIds)
            {
                await redis.KeyDeleteAsync([new RedisKey($"player_ranked_set:{id}"), new RedisKey($"ranked_disconnect:{id}")]);
            }

            return GameEndResult.Of(GameEnd.Crashed);
        }

        if (!counts)
        {
            return GameEndResult.Of(GameEnd.NotASet);
        }

        string lockId = existingSetId ?? matchId;
        await using var held = await LockAsync(redis, lockId);
        if (held is null)
        {
            log.LogError("Set {Set} stayed locked for {Wait} s: the end of match {Match} was not counted", lockId, LockWait.TotalSeconds, matchId);
            return GameEndResult.Of(GameEnd.Resolved);
        }

        var existingSet = existingSetId is null ? null : await SetAsync(redis, existingSetId);
        if (existingSetId is not null && existingSet is null)
        {
            log.LogInformation("Stale player_ranked_set (set {Set} no longer exists), cleaning up", existingSetId);
            foreach (string id in playerIds)
            {
                await redis.KeyDeleteAsync($"player_ranked_set:{id}");
            }

            existingSetId = null;
        }

        if (existingSetId is null && !(await redis.StringGetAsync($"ranked_set_pending_winner:{matchId}")).HasValue)
        {
            log.LogWarning("Match {Match} has no set and no pending winner — orphan match, no set made; players back to the menus", matchId);
            foreach (string id in playerIds)
            {
                await redis.KeyDeleteAsync([new RedisKey($"player_ranked_set:{id}"), new RedisKey($"ranked_disconnect:{id}")]);
            }

            return GameEndResult.Of(GameEnd.Orphan);
        }

        string setId = existingSetId ?? matchId;
        if ((string?)await redis.StringGetAsync($"elo_processed_set:{setId}") is { } resolved)
        {
            log.LogInformation("Set {Set} already resolved ({How}), nothing to update for match {Match}", setId, resolved, matchId);
            return GameEndResult.Of(GameEnd.Resolved);
        }

        // gamesPlayed + 1 and the scores as stored (RecordWinnerAsync counted this game's winner into them); a game 1
        // whose set was never made counts its pending winner.
        double gamesPlayed = existingSet is null ? 1 : Number(existingSet["gamesPlayed"]) + 1;
        var scores = existingSet?["scores"] is JsonArray stored && RollbackCallbacks.Truthy(stored) ? stored.DeepClone().AsArray() : new JsonArray(0, 0);
        if (existingSet is null && (string?)await redis.StringGetAsync($"ranked_set_pending_winner:{matchId}") is { } pending)
        {
            double winIndex = Js.ParseInt(pending);
            if (winIndex is 0 or 1)
            {
                scores = new JsonArray(0, 0);
                scores[(int)winIndex] = 1;
            }

            await redis.KeyDeleteAsync($"ranked_set_pending_winner:{matchId}");
            log.LogWarning("FALLBACK: match {Match} ended with no set — recovered via pending_winner={Winner}", matchId, pending);
        }

        double team0 = Number(scores.Count > 0 ? scores[0] : null), team1 = Number(scores.Count > 1 ? scores[1] : null);
        if (team0 >= 2 || team1 >= 2 || gamesPlayed >= 3)
        {
            return await SetOverAsync(redis, existingSet, setId, matchId, playerIds, team0, team1);
        }

        // A player of this set whose flag names it left mid-game: the set is conceded at the check-in.
        string? dodger = null;
        foreach (string id in playerIds)
        {
            if ((string?)await redis.StringGetAsync($"ranked_disconnect:{id}") == setId)
            {
                dodger = id;
                log.LogInformation("Match {Match} — player {Player} dodged, set will be marked conceded", matchId, id);
                break;
            }
        }

        var state = new JsonObject
        {
            ["players"] = config?["players"]?.DeepClone(),
            ["mode"] = config?["mode"]?.DeepClone(),
            ["gamesPlayed"] = gamesPlayed,
            ["scores"] = scores,
            ["checkins"] = new JsonArray(),
        };
        if (dodger is not null)
        {
            state["conceded"] = true;
            state["concedingPlayer"] = dodger;
        }

        await redis.StringSetAsync($"ranked_set:{setId}", Js.Stringify(state), s_setTtl);
        foreach (string id in (config?["players"] as JsonArray ?? []).Select(p => Text(p?["playerId"])).OfType<string>())
        {
            await redis.StringSetAsync($"player_ranked_set:{id}", setId, s_setTtl);
        }

        log.LogInformation("Ranked set {Set} — game {Games}/3 complete ({Team0}-{Team1}), waiting for check-ins", setId, gamesPlayed, team0, team1);
        return GameEndResult.Of(GameEnd.Continues);
    }

    // The set is over: rated once (elo_processed_set "set_complete"), the winner the team with more wins, each fighter
    // this game's (match_characters:{match}, else game 1's, else the connection); then dropped.
    private async Task<GameEndResult> SetOverAsync(IDatabase redis, JsonObject? set, string setId, string matchId, IReadOnlyList<string> playerIds, double team0Wins, double team1Wins)
    {
        log.LogInformation("Ranked set {Set} — set complete ({Team0}-{Team1}), processing ELO", setId, team0Wins, team1Wins);
        int winnerTeam = team0Wins > team1Wins ? 0 : 1;
        var players = set?["players"] as JsonArray ?? [];
        var team0 = players.Where(p => Number(p?["teamIndex"]) == 0).Select(p => Text(p?["playerId"]) ?? "").ToList();
        var team1 = players.Where(p => Number(p?["teamIndex"]) == 1).Select(p => Text(p?["playerId"]) ?? "").ToList();
        var winners = winnerTeam == 0 ? team0 : team1;
        var losers = winnerTeam == 0 ? team1 : team0;
        bool first = await redis.StringSetAsync($"elo_processed_set:{setId}", "set_complete", TimeSpan.FromMinutes(5), When.NotExists);
        if (first && set is null)
        {
            // Never at 2 wins or 3 games; TS threw reading its players, and rated nobody.
            log.LogError("Set {Set} is over but its state is gone: not rated", setId);
        }
        else if (first)
        {
            try
            {
                await ratings.RateAsync(new SetOutcome(winners, losers, Text(set?["mode"]) ?? "", (int)team0Wins, (int)team1Wins, winnerTeam, false,
                    await CharactersAsync(redis, [.. winners, .. losers], setId, matchId), matchId), CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log.LogError("Error processing set ELO for set {Set}: {Error}", setId, e.Message);
            }
        }
        else
        {
            log.LogInformation("ELO already processed for set {Set}, skipping set-complete processing", setId);
        }

        var setPlayers = set is null ? [.. playerIds.Distinct()] : PlayerIds(set);
        await DropAsync(redis, setId, setPlayers, disconnectFlags: false);
        return new GameEndResult(GameEnd.Over, setPlayers, [.. winners, .. losers]);
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

    // Rates the set for winnerTeam, then sends each player their ranks (FullRankUpdate, as the TS websocket did for
    // ranked_set:fullrankupdate; to every player but bots, connected or not: a result is a result). A failure is logged
    // and announces nothing, as there.
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

            if (services.GetService<IMongoDatabase>() is { } mongo)
            {
                var humans = (set["players"] as JsonArray ?? []).Where(p => !Truthy(p?["isBot"])).Select(p => Text(p?["playerId"])).OfType<string>().Distinct();
                await FullRankUpdate.SendAsync(redis, mongo, eloRatings, humans, season.CurrentValue.Current, FullRankUpdateVariant.SetResult,
                    time, log, CancellationToken.None);
            }
            else
            {
                log.LogError("No FullRankUpdate for set {Set}: this service has no Mongo (MONGODB_URI)", setId);
            }
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
        // Announced once the set's keys name this game (a game that cannot be told ends its set: MatchLaunches). A set's
        // next game sends no matchmaking-complete (the TS server sent none).
        await MatchLaunches.AnnounceAsync(redis, matchId, Js.Stringify(notification), []);
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
        await redis.KeyDeleteAsync($"ranked_set_crashed:{setId}");
    }

    // MatchSetLeaverNotification to every player (AccountId: the leaver) after `after`, then the empty config 500 ms later,
    // which sends the game back to its menus: the TS websocket's ranked_set:leaver handler.
    private async Task LeaverAsync(IDatabase redis, List<string> all, string leaver, string setId, TimeSpan after)
    {
        foreach (string id in all)
        {
            var message = ProfileNotifications.Message(new JsonObject { ["AccountId"] = leaver, ["MatchId"] = setId, ["template_id"] = "MatchSetLeaverNotification" }, id);
            if (after > TimeSpan.Zero)
            {
                await DelayedMessages.ScheduleAsync(redis, time, [id], message, after);
            }
            else
            {
                await PlayerMessages.SendAsync(redis, [id], message);
            }
        }

        await DelayedMessages.ScheduleAsync(redis, time, all, MatchEnd.EmptyConfig(), after + s_emptyConfigDelay);
        log.LogInformation("MatchSetLeaverNotification for set {Set} (leaver {Leaver}) sent to {Count} players", setId, leaver, all.Count);
    }

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
        builder.AddEloRatings();
        builder.AddSetting<SeasonSettings>("Season");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IRankedSets, RankedSets>();
        return builder;
    }
}
