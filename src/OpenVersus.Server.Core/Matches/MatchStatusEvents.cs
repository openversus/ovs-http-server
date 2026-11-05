using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Leaderboards;
using MongoDB.Driver;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A rollback server's match status events (POST /api/ovs_match_status, /ovs_match_status), ported from the TS server's
// server.ts route and handlers/match_status.ts (handleMatchStatusUpdate, handlePlayerDisconnectElo), branch
// infinity-war. Every event of a match is posted (ovs-rollback-server, Core/Events.cs: MatchStatus, PascalCase, with
// matchId and key), never awaited; the answer is read as a JSON object and nothing in it is used. Checked by the
// MatchUpdateKey header (MatchUpdateKeys), not the match key: these events decide ratings. P2P nodes send none (no key on
// a player's machine: FireMatchEvents is off there).
//
//   MatchStarted            match_started:{match}: a disconnect from now on is mid-game (the rollback server sends it again
//                           at every Ready once all are ready; each one sets it again)
//   TerminatingError        after the start: the match crashed (match_server_crash:{match}): not rated
//   AllPlayersDisconnected  after the start and before any result (game_result_received:{match}): a crash too
//   MatchEnded              match_ended:{match}; match_started:{match} deleted
//   PlayerDisconnect        a player's connection dropped (not "Unknown"), and none of: the match ended, a result came,
//                           it crashed, the player is a spectator. Then
//     - their websocket is still up (online_players): the rollback server failed them, not a dodge. Once per match
//       (rollback_crash_cleanup:{match}): the match crashed, the set is dropped for every player of it, all are set idle
//       and told the match was cancelled ("rollback_crash"); no rating; a custom lobby's ready flags come down.
//     - after the start, in a game RatedMatches counts (a set game): ranked_disconnect:{player} = the set's id
//       (player_ranked_set, else the match: game 1), so the set's next check-in concedes for them (RankedSets); the flag
//       counts for that set only.
//     - before the start (a dodge): the match is called off (match_called_off:{match}: any later leave of it changes
//       nothing). The other team wins the set (rated when RatedMatches says it counts: SetRatings, a pregame dodge, each
//       player's FullRankUpdate, and the dodger's ranked_disconnect naming the set), the set is dropped, every human of
//       the match, spectators too, is set idle and the others are told ("opponent_dodge"). A custom game (a custom
//       lobby's match or a Casual one) is never rated and has no set: its end is claimed instead (match_end:{match},
//       MatchEnd's once-key), so a late /ovs_end_match opens no rematch vote, and a custom lobby's ready flags come down
//       as at a match's end. Once per set and match (elo_processed_set:{set} "rollback_pregame_dodge" NX EX 5 min,
//       elo_processed:{match} NX EX 5 min: the TS websocket's disconnect path checks the same keys).
// Answered {status: "ok"}; 403 {error: "Invalid signature"} without the key; 500 {error: "Failed to process match status
// update"} when handling failed (a PlayerDisconnect's failure is logged, not answered).
//
// A player's game closing its websocket (GameClosedAsync, from the realtime gateway's disconnects: MatchDisconnects) is
// the TS websocket's close for the match (websocket.ts 511-616), and the only such signal for a P2P match: the match is the
// one the game was sent last (match_config:{player}, GameplayConfigs), handled as a PlayerDisconnect but for the online
// check (the dodge's once-per-set key says "pregame_dodge", as there); then, while the player is still in a ranked set
// (between its games, after a game's result), ranked_disconnect:{player} names it, so its next check-in concedes it.
// A close the gateway made for a node that is gone (GatewayReaper: the node died with the game's socket) is the server's
// failure, not the player's: a match before its result is a crash (as a rollback server losing a player whose websocket
// is up: canceled for everyone, no rating), and a set between its games is marked (ranked_set_crashed:{set}) so its next
// check-in drops it unrated (RankedSets); a set that ends on its results is rated all the same.
//
// Redis, read     match_started:{match}, match_ended:{match}, game_result_received:{match}, match_server_crash:{match},
//                 {match} (players, mode, isCustomGame), match:{match} (RatedMatches), online_players,
//                 player_ranked_set:{player}, elo_processed:{match}, match_characters:{set}, connections:{player} character;
//                 match_config:{player} (a websocket close); match_called_off:{match}; ssc_custom_lobby_match:{match}
// Redis, written  match_started:{match}, match_ended:{match}, match_server_crash:{match} "1" EX 10 min;
//                 rollback_crash_cleanup:{match} NX EX 5 min; ranked_disconnect:{player} (the set's id) EX 10 min;
//                 ranked_set_crashed:{set} "gateway_node_gone" EX 10 min (a reaped close between a set's games); the dedup keys
//                 above; match_called_off:{match} (the dodge's reason) EX 20 min, the match's TTL; at a custom game's
//                 call-off match_end:{match} "called_off" NX EX 20 min, and custom_lobby_ssc:{lobby} ReadyPlayers emptied
//                 (CustomLobbyService.MatchEndedAsync, the crash's too) with ssc_custom_lobby_match:{match} deleted;
//                 player:{player} status "idle"; dll_notifications:{player} (match_cancel, PlayerMessages);
//                 deleted: player_ranked_set:{each player}, ranked_set:{set}, ranked_set_checkins:{set},
//                 ranked_set_match:{set}, and at a crash match_to_set:{match}, match_started:{match}
// Sent (ws:send)  FullRankUpdate (FullRankUpdateVariant.SetResult) after a rating, to the match's players but bots,
//                 connected or not, as the TS websocket built it from ranked_set:fullrankupdate (FullRankUpdate.SendAsync)
// Mongo, written  eloratings, playerstats (SetRatings); eloratings for a player with none (FullRankUpdate)
//
// Unlike there:
//   - a spectator's disconnect changes nothing (decided 2026-10-05). TS asked "still online?" before "spectator?", so a
//     spectator whose connection dropped while their websocket stayed up crashed the match: set dropped, every player sent
//     a match_cancel mid-match.
//   - a dodge is rated only when RatedMatches says the match counts (MIGRATION-BRIDGES.md 6; TS rated a rift match, and
//     any match with bots); the rest of it runs as there (decided 2026-10-05: only the rating is skipped).
//   - a dropped set's current game (ranked_set_match:{set}, RankedSets's) is deleted with it.
//   - ranked_disconnect is written only in a rated set's game and holds the set's id (decided 2026-10-05; TS wrote "1" after
//     any started match, custom and Casual ones included, and its readers took any flag: a stale one conceded the
//     player's next set).
//   - an unset key, or the TS placeholder, never matches (MatchUpdateKeys).
//   - a custom game's pregame leave calls the match off as a dodge does, unrated (decided 2026-10-07; TS left it alone:
//     everyone else sat at perk select, waiting for the leaver's lock). A dodge releases the spectators too.
//   - a called-off match stays called off: TS ran a released player's own leave from the rollback server as a crash
//     (a second cancel), and a close of theirs after the 5-minute dedup keys as another dodge, rated against them.
//   - a player is set idle only while their record (player:{player}) is there: TS wrote one holding the status alone
//     for a player whose keys its own close had deleted.
//   - a websocket close: a spectator's changes nothing (TS took the spectator's team for the dodger's, and rated a set
//     against a team when its spectator closed the game before the start); a match that crashed is left to the crash's
//     cleanup (TS dropped the set and told the others again); the flag names the set for 10 minutes (TS wrote "1" for 10
//     minutes, then the set's id for 2: a mid-game leaver's flag ran out before the game's end, when it is read).
//   - a close the gateway made for a node that died (GatewayReaper) is a crash, never a dodge or a leave: TS had one
//     websocket process, whose crash ran no close at all.

public interface IMatchStatusEvents
{
    /// <summary>One status event (<paramref name="body"/>) with its MatchUpdateKey header; the status and answer.
    /// <paramref name="from"/> is the caller's address, for the log.</summary>
    Task<(int Status, JsonObject Answer)> HandleAsync(string? matchUpdateKey, JsonNode? body, string? from);

    /// <summary>
    /// <paramref name="playerId"/>'s game closed its websocket (MatchDisconnects, the realtime gateway's disconnects):
    /// what the TS websocket's close did for the match (websocket.ts 511-616; see the header). <paramref name="nodeGone"/>:
    /// the gateway node holding it died and another closed it (GatewayReaper), the server's failure.
    /// </summary>
    Task GameClosedAsync(string playerId, bool nodeGone = false);

    /// <summary>
    /// The game said over HTTP that <paramref name="playerId"/> is leaving <paramref name="matchId"/> (PUT
    /// /matches/{id}/leave, through the match:results stream): settled as its websocket close would be, once; a
    /// close or a rollback PlayerDisconnect for the same leave changes nothing more (every effect is keyed per match).
    /// </summary>
    Task LeftAsync(string matchId, string playerId);
}

internal sealed class MatchStatusEvents(IServiceProvider services, ISetRatings ratings, EloRatings eloRatings, IOptionsMonitor<RollbackSettings> settings,
    IOptionsMonitor<SeasonSettings> season, TimeProvider time, ILogger<MatchStatusEvents> log) : IMatchStatusEvents
{
    private static readonly HashSet<string> s_quiet = ["TickPerformance", "HeartBeat"];
    private static readonly TimeSpan s_flagTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_dedupTtl = TimeSpan.FromMinutes(5);
    // A match's keys live 20 minutes (its config, match_config:{player}): a mark about the match outlives them.
    private static readonly TimeSpan s_matchTtl = TimeSpan.FromMinutes(20);

    public async Task<(int Status, JsonObject Answer)> HandleAsync(string? matchUpdateKey, JsonNode? body, string? from)
    {
        if (MatchUpdateKeys.Refusal(matchUpdateKey, settings.CurrentValue.MatchUpdateKey) is { } refusal)
        {
            if (refusal == "not configured")
            {
                log.LogError("POST /api/ovs_match_status rejected: no Rollback:MatchUpdateKey (MATCHUPDATEKEY) is configured");
            }
            else
            {
                log.LogWarning("POST /api/ovs_match_status rejected: {State} MatchUpdateKey from {From}", refusal, from ?? "unknown");
            }

            return (403, new JsonObject { ["error"] = "Invalid signature" });
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Match status not handled: this service has no Redis (REDIS)");
            return (500, new JsonObject { ["error"] = "Failed to process match status update" });
        }

        try
        {
            await HandleEventAsync(redis, body as JsonObject ?? []);
            return (200, new JsonObject { ["status"] = "ok" });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogError("Error handling match status update: {Error}", e.Message);
            return (500, new JsonObject { ["error"] = "Failed to process match status update" });
        }
    }

    private async Task HandleEventAsync(IDatabase redis, JsonObject status)
    {
        string ev = Str(status["Event"]);
        string description = Str(status["Description"]);
        string matchId = Str(status["matchId"]);
        string playerId = Str(status["PlayerId"]);
        var playerIds = status["PlayerIds"] is JsonArray ids ? ids.Select(Str).ToList() : [];

        if (!s_quiet.Contains(ev))
        {
            log.LogInformation("Event={Event}, MatchId={Match}, NumPlayers={NumPlayers}, PlayerId={Player}, PlayerIds={Players}, Description={Description}",
                ev, matchId, status["NumPlayers"]?.ToJsonString() ?? "0", playerId, string.Join(",", playerIds), description);
        }

        if (ev.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Received {Event} for match {Match}: {Description}", ev, matchId, description);
        }

        if (ev == "TickPerformance" && description.Contains(": 1668", StringComparison.Ordinal))
        {
            log.LogWarning("High tick time for match {Match}: {Description}", matchId, description);
        }

        if (matchId.Length == 0)
        {
            return;
        }

        switch (ev)
        {
            case "MatchStarted":
                await redis.StringSetAsync($"match_started:{matchId}", "1", s_flagTtl);
                log.LogInformation("Set match_started:{Match}: a mid-game disconnect no longer cancels it", matchId);
                break;

            case "TerminatingError":
                if (await redis.KeyExistsAsync($"match_started:{matchId}"))
                {
                    await redis.StringSetAsync($"match_server_crash:{matchId}", "1", s_flagTtl);
                    log.LogWarning("Server crashed mid-match {Match}: it will not be rated", matchId);
                }

                break;

            case "AllPlayersDisconnected":
                if (await redis.KeyExistsAsync($"match_started:{matchId}") && !await redis.KeyExistsAsync($"game_result_received:{matchId}"))
                {
                    await redis.StringSetAsync($"match_server_crash:{matchId}", "1", s_flagTtl);
                    log.LogWarning("All players disconnected from match {Match} without a game result: treated as a crash, not rated", matchId);
                }

                break;

            case "MatchEnded":
                await redis.StringSetAsync($"match_ended:{matchId}", "1", s_flagTtl);
                await redis.KeyDeleteAsync($"match_started:{matchId}");
                break;

            case "PlayerDisconnect" when playerId.Length > 0 && playerId != "Unknown":
                await LeftAsync(redis, matchId, playerId, playerIds, fromRollback: true);
                break;
        }
    }

    public async Task GameClosedAsync(string playerId, bool nodeGone = false)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Websocket close of {Player} not handled: this service has no Redis (REDIS)", playerId);
            return;
        }

        // The match the game was in: the config it was sent last (GameplayConfigs), kept until the match's end.
        if (await RollbackCallbacks.JsonAsync(redis, $"{GameplayConfigs.KeyPrefix}{playerId}") is { } sent
            && Str(sent["data"]?["GameplayConfig"]?["MatchId"]) is { Length: > 0 } matchId)
        {
            await LeftAsync(redis, matchId, playerId, [], fromRollback: false, nodeGone);
        }

        // A set the player is still in (between its games, or after a game's result): its next check-in concedes it for
        // them, or, when their node died, drops it unrated. Gone after a dodge or a crash, which dropped the set.
        if ((string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is { Length: > 0 } setId)
        {
            if (nodeGone)
            {
                await redis.StringSetAsync($"ranked_set_crashed:{setId}", "gateway_node_gone", s_flagTtl);
                log.LogWarning("Websocket of {Player} lost with its gateway node during ranked set {Set}: the set is dropped at its next check-in, no rating",
                    playerId, setId);
                return;
            }

            await redis.StringSetAsync($"ranked_disconnect:{playerId}", setId, s_flagTtl);
            log.LogInformation("Websocket close of {Player} during ranked set {Set}: flagged for auto-concede", playerId, setId);
        }
    }

    public async Task LeftAsync(string matchId, string playerId)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Leave of {Player} from {Match} not handled: this service has no Redis (REDIS)", playerId, matchId);
            return;
        }

        await LeftAsync(redis, matchId, playerId, [], fromRollback: false, what: "Leave (said over HTTP)");
    }

    // A player left a match: a PlayerDisconnect from its rollback server, their game closed its websocket (or the gateway
    // node holding it died: nodeGone), or the game said so over HTTP (what).
    private async Task LeftAsync(IDatabase redis, string matchId, string playerId, List<string> eventPlayerIds, bool fromRollback, bool nodeGone = false, string? what = null)
    {
        what ??= fromRollback ? "PlayerDisconnect" : nodeGone ? "Websocket loss (gateway node gone)" : "Websocket close";
        try
        {
            if (await redis.KeyExistsAsync($"match_ended:{matchId}"))
            {
                return;
            }

            // A result came (submit_end_of_match_stats): the game is over and everyone leaves, which is not a dodge.
            if (await redis.KeyExistsAsync($"game_result_received:{matchId}"))
            {
                log.LogInformation("{What} of {Player} in {Match} after its result: a normal leave, nothing to do", what, playerId, matchId);
                return;
            }

            if (await redis.KeyExistsAsync($"match_server_crash:{matchId}"))
            {
                return;
            }

            if (await redis.KeyExistsAsync(CalledOffKey(matchId)))
            {
                log.LogInformation("{What} of {Player} in {Match}: the match was called off already, nothing to do", what, playerId, matchId);
                return;
            }

            var config = await RollbackCallbacks.JsonAsync(redis, matchId);
            var configPlayers = (config?["players"] as JsonArray)?.OfType<JsonObject>().ToList();
            if (configPlayers?.FirstOrDefault(p => Str(p["playerId"]) == playerId) is { } entry && RollbackCallbacks.Truthy(entry["isSpectator"]))
            {
                log.LogInformation("{What} of spectator {Player} in {Match}: a spectator leaving changes nothing for the match", what, playerId, matchId);
                return;
            }

            // The rollback server lost a player whose game is still connected: its failure, not theirs. (A websocket close
            // is the game going; one that came back since is not handled at all: MatchDisconnects.)
            if (fromRollback && await redis.SetContainsAsync("online_players", playerId))
            {
                await RollbackCrashAsync(redis, matchId, playerId, configPlayers, eventPlayerIds, "PlayerDisconnect with the websocket still up: a rollback server issue");
                return;
            }

            // The gateway node holding the game's websocket died: the server failed the player, whatever stage the match is at.
            if (nodeGone)
            {
                await RollbackCrashAsync(redis, matchId, playerId, configPlayers, eventPlayerIds, "Websocket lost with its gateway node: a server issue");
                return;
            }

            // Mid-game: the others play on; in a rated set, its next check-in (or the game's end) concedes it for the leaver
            // (RankedSets): the flag names the set, so it never counts against another one.
            if (await redis.KeyExistsAsync($"match_started:{matchId}"))
            {
                string? why = config is null ? "no config"
                    : RatedMatches.WhyNotRated(Str(config["mode"]), config["players"] as JsonArray, await RollbackCallbacks.JsonAsync(redis, $"match:{matchId}"), config);
                if (why is not null)
                {
                    log.LogInformation("Mid-match {What} of {Player} in {Match}: not a set game ({Why}), nothing to concede", what, playerId, matchId, why);
                    return;
                }

                string set = (string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is { Length: > 0 } pointer ? pointer : matchId;
                await redis.StringSetAsync($"ranked_disconnect:{playerId}", set, s_flagTtl);
                log.LogInformation("Mid-match {What} of {Player} in {Match}: flagged for auto-concede of set {Set}", what, playerId, matchId, set);
                return;
            }

            await PregameDodgeAsync(redis, matchId, playerId, config, configPlayers, fromRollback ? "rollback_pregame_dodge" : "pregame_dodge");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogError("Error processing {What} of {Player} in {Match}: {Error}", what, playerId, matchId, e.Message);
        }
    }

    // The server failed the player: the rollback server lost them while their websocket is still up (a crash, a level that
    // did not load, a UDP timeout; real dodges close the websocket), or the gateway node holding their websocket died.
    private async Task RollbackCrashAsync(IDatabase redis, string matchId, string playerId, List<JsonObject>? configPlayers, List<string> eventPlayerIds, string cause)
    {
        if (!await redis.StringSetAsync($"rollback_crash_cleanup:{matchId}", "1", s_dedupTtl, When.NotExists))
        {
            log.LogInformation("{Cause} ({Player} in {Match}): cleanup already running", cause, playerId, matchId);
            return;
        }

        await redis.StringSetAsync($"match_server_crash:{matchId}", "1", s_flagTtl);
        log.LogWarning("{Cause} ({Player} in {Match}). Dropping the set, no rating", cause, playerId, matchId);

        string setId = (string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is { Length: > 0 } set ? set : matchId;
        var all = configPlayers is not null
            ? configPlayers.Where(p => !RollbackCallbacks.Truthy(p["isSpectator"])).Select(p => Str(p["playerId"])).ToList()
            : eventPlayerIds;

        foreach (string id in all)
        {
            await redis.KeyDeleteAsync($"player_ranked_set:{id}");
        }

        await redis.KeyDeleteAsync($"ranked_set:{setId}");
        await redis.KeyDeleteAsync($"ranked_set_checkins:{setId}");
        await redis.KeyDeleteAsync($"ranked_set_match:{setId}");
        await redis.KeyDeleteAsync($"match_to_set:{matchId}");
        await redis.KeyDeleteAsync($"match_started:{matchId}");

        await LobbyReadyResetAsync(redis, matchId);
        await IdleAsync(redis, all);
        await CancelAsync(redis, all, matchId, "Server connection failed — no ELO change", "rollback_crash");
        log.LogInformation("Dropped set {Set} for {Players} player(s) after a rollback crash (no rating)", setId, all.Count);
    }

    // The games a set's state says it finished: its gamesPlayed, or its score's sum when that is more.
    private static int GamesFinished(JsonObject? set)
    {
        if (set is null)
        {
            return 0;
        }

        static int Count(JsonNode? value) => value is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? (int)d : 0;
        var scores = set["scores"] as JsonArray;
        return Math.Max(Count(set["gamesPlayed"]), Count(scores?.ElementAtOrDefault(0)) + Count(scores?.ElementAtOrDefault(1)));
    }

    // Before the start: the leaver dodged and the match is called off; the other team wins the set (a custom game: no set,
    // no rating, its end claimed).
    private async Task PregameDodgeAsync(IDatabase redis, string matchId, string playerId, JsonObject? config, List<JsonObject>? configPlayers, string reason)
    {
        if (config is null || configPlayers is null)
        {
            return;
        }

        if (configPlayers.FirstOrDefault(p => Str(p["playerId"]) == playerId) is not { } leaver || RollbackCallbacks.Truthy(leaver["isSpectator"]))
        {
            return;
        }

        bool custom = RollbackCallbacks.Truthy(config["isCustomGame"]);
        int winnerTeam = IsNumber(leaver["teamIndex"], 0) ? 1 : 0;
        var team0 = configPlayers.Where(p => IsNumber(p["teamIndex"], 0) && !RollbackCallbacks.Truthy(p["isSpectator"])).Select(p => Str(p["playerId"])).ToList();
        var team1 = configPlayers.Where(p => IsNumber(p["teamIndex"], 1) && !RollbackCallbacks.Truthy(p["isSpectator"])).Select(p => Str(p["playerId"])).ToList();
        var winners = winnerTeam == 0 ? team0 : team1;
        var losers = winnerTeam == 0 ? team1 : team0;

        // A custom game is in no set, whatever pointer a player still holds.
        string setId = !custom && (string?)await redis.StringGetAsync($"player_ranked_set:{playerId}") is { Length: > 0 } set ? set : matchId;
        if (await redis.KeyExistsAsync($"elo_processed:{matchId}"))
        {
            return;
        }

        if (!await redis.StringSetAsync($"elo_processed_set:{setId}", reason, s_dedupTtl, When.NotExists))
        {
            return;
        }

        await redis.StringSetAsync($"elo_processed:{matchId}", "1", s_dedupTtl, When.NotExists);
        await redis.StringSetAsync(CalledOffKey(matchId), reason, s_matchTtl);

        string? mode = config["mode"] is JsonValue m && m.TryGetValue(out string? text) ? text : null;
        if (custom)
        {
            log.LogInformation("Pregame leave of {Player} in custom game {Match} ({Reason}): called off, no rating", playerId, matchId, reason);
        }
        else if (RatedMatches.WhyNotRated(mode, config["players"] as JsonArray, await RollbackCallbacks.JsonAsync(redis, $"match:{matchId}"), config) is { } why)
        {
            log.LogWarning("Pregame dodge of {Player} in {Match} not rated: {Why}", playerId, matchId, why);
        }
        else
        {
            var characters = await CharactersAsync(redis, [.. winners, .. losers], setId);
            // End Game's set XP: the dodger is the quitter, and the games the set finished before decide whether it pays.
            int gamesBefore = setId == matchId ? 0 : GamesFinished(await RollbackCallbacks.JsonAsync(redis, $"ranked_set:{setId}"));
            await ratings.RateAsync(new SetOutcome(winners, losers, mode!, 0, 0, winnerTeam, IsConcede: true, characters, matchId, IsPregameDodge: true,
                QuitterIds: [playerId], GamesBeforeDodge: gamesBefore), CancellationToken.None);
            log.LogInformation("Pregame dodge rated ({Reason}): {Player} left match {Match}", reason, playerId, matchId);
            if (services.GetService<IMongoDatabase>() is { } mongo)
            {
                await FullRankUpdate.SendAsync(redis, mongo, eloRatings, configPlayers.Where(p => !RollbackCallbacks.Truthy(p["isBot"])).Select(p => Str(p["playerId"])),
                    season.CurrentValue.Current,
                    FullRankUpdateVariant.SetResult, time, log, CancellationToken.None);
            }
            else
            {
                log.LogError("No FullRankUpdate after the dodge in {Match}: this service has no Mongo (MONGODB_URI)", matchId);
            }

            // The dodger's flag names the set it dropped, so it never counts against their next one.
            await redis.StringSetAsync($"ranked_disconnect:{playerId}", setId, s_flagTtl);
        }

        if (custom)
        {
            // A late /ovs_end_match opens no rematch vote (MatchEnd's once-key); the lobby is out of its match, every ready
            // flag down, as at a match's end.
            await redis.StringSetAsync($"match_end:{matchId}", "called_off", s_matchTtl, When.NotExists);
            await LobbyReadyResetAsync(redis, matchId);
            await redis.KeyDeleteAsync($"ssc_custom_lobby_match:{matchId}");
        }
        else
        {
            foreach (string id in configPlayers.Where(p => !RollbackCallbacks.Truthy(p["isSpectator"])).Select(p => Str(p["playerId"])))
            {
                await redis.KeyDeleteAsync($"player_ranked_set:{id}");
            }

            if (setId != matchId)
            {
                await redis.KeyDeleteAsync($"ranked_set:{setId}");
                await redis.KeyDeleteAsync($"ranked_set_checkins:{setId}");
                await redis.KeyDeleteAsync($"ranked_set_match:{setId}");
            }
        }

        // Every human of the match is released, spectators too: they wait on the same screen.
        var all = configPlayers.Select(p => Str(p["playerId"])).ToList();
        await IdleAsync(redis, all);
        await CancelAsync(redis, all.Where(id => id != playerId), matchId, "Opponent left the match", "opponent_dodge");
        if (custom)
        {
            log.LogInformation("Called off custom game {Match} for {Players} player(s) after a pregame leave", matchId, all.Count);
        }
        else
        {
            log.LogInformation("Dropped set {Set} for {Players} player(s) after a pregame dodge", setId, all.Count);
        }
    }

    private static string CalledOffKey(string matchId) => $"match_called_off:{matchId}";

    // A custom lobby's match over without its end (MatchEnd): the lobby's ready flags come down as at a match's end
    // (CustomLobbyService.MatchEndedAsync), so the next start needs everyone ready again.
    private static async Task LobbyReadyResetAsync(IDatabase redis, string matchId)
    {
        if ((string?)await redis.StringGetAsync($"ssc_custom_lobby_match:{matchId}") is { Length: > 0 } lobbyId)
        {
            await CustomLobbyService.MatchEndedAsync(redis, lobbyId);
        }
    }

    // Each player's fighter: the set's match_characters, then their connection.
    private static async Task<Dictionary<string, string>> CharactersAsync(IDatabase redis, IEnumerable<string> playerIds, string setId)
    {
        var characters = new Dictionary<string, string>();
        var stored = await RollbackCallbacks.JsonAsync(redis, $"match_characters:{setId}");
        foreach (string id in playerIds)
        {
            if (RollbackCallbacks.Text(stored?[id]) is { } character)
            {
                characters[id] = character;
            }
            else if ((string?)await redis.HashGetAsync($"connections:{id}", "character") is { Length: > 0 } connected)
            {
                characters[id] = connected;
            }
        }

        return characters;
    }

    // Every player idle, so a stale "in_match" does not block their next invite; a failure is logged per player.
    // Only while the player's record is there: a player whose session is gone (their disconnect's cleanup, which may run
    // before or after this) gets no record holding a status alone.
    private async Task IdleAsync(IDatabase redis, IEnumerable<string> playerIds)
    {
        foreach (string id in playerIds)
        {
            try
            {
                var idle = redis.CreateTransaction();
                idle.AddCondition(Condition.KeyExists($"player:{id}"));
                _ = idle.HashSetAsync($"player:{id}", "status", "idle");
                await idle.ExecuteAsync();
            }
            catch (RedisException e)
            {
                log.LogError("Error resetting status for {Player}: {Error}", id, e.Message);
            }
        }
    }

    // A match_cancel notification for the OpenVersus client of each player; a failure is logged per player.
    private async Task CancelAsync(IDatabase redis, IEnumerable<string> playerIds, string matchId, string message, string reason)
    {
        foreach (string id in playerIds)
        {
            try
            {
                await PlayerMessages.NotifyClientAsync(redis, id, "match_cancel", "Match Cancelled", message,
                    new JsonObject { ["matchId"] = matchId, ["reason"] = reason }, time.GetUtcNow().ToUnixTimeMilliseconds());
                log.LogInformation("Queued a {Reason} match_cancel for {Player}", reason, id);
            }
            catch (RedisException e)
            {
                log.LogError("Error queueing the {Reason} match_cancel for {Player}: {Error}", reason, id, e.Message);
            }
        }
    }

    // A field as the TS handler reads it (value ?? ""): a string as it is, anything else as JavaScript prints it.
    private static string Str(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => value.ToJsonString(),
    };

    // value === number in JavaScript.
    private static bool IsNumber(JsonNode? value, int number) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && Js.Number(v.ToJsonString()) == number;
}

public static class MatchStatusEventsHosting
{
    /// <summary>The rollback servers' match status events (and the set ratings a dodge needs).</summary>
    public static WebApplicationBuilder AddMatchStatusEvents(this WebApplicationBuilder builder)
    {
        builder.AddSetRatings();
        builder.AddEloRatings();
        builder.AddSetting<SeasonSettings>("Season");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IMatchStatusEvents, MatchStatusEvents>();
        return builder;
    }
}
