using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// PUT /ssc/invoke/submit_end_of_match_stats {ContainerMatchId, EndOfMatchStats: {PlayerMissionUpdates, PlayerNetworkStats,
// Score, WinningTeamIndex}, MatchLength}, ported from the TS server's handleSsc_invoke_submit_end_of_match_stats
// (handlers/ssc.ts), branch infinity-war. Each human's game sends it when its match is over, before EndOfMatchPayload:
// the rollback server ends the match (/ovs_end_match, then match end) only once every player has left it, seconds to
// a minute after their reports (three matches on the bench, 2026-09-30 and 10-04). It carries the match as the game
// simulated it: the winner and every player's counters.
//
// Before the answer (the set's check-ins read it):
//   game_result_received:{match} NX EX 10 min (the TS rollback callbacks tell a crash from a finished game by it)
//   the report, kept with the others (match_results:{match}, a hash: player -> {order, spectator, winner, stats}, the
//     first report of each player only; EX 10 min), and the winner decided from all of them (MatchWinner). A set game
//     (one RatedMatches counts): that winner into the set score (RankedSets.RecordWinnerAsync; the reporter's set, else
//     another player's of the match), as the TS server counted the first report's into the reporter's
//   the result on the match flow's stream (match:results, a consumer group; the match flow records missions, match XP,
//     rift progress, and the match's stats once every human has reported or 30 s after the first:
//     match_results:due {match} -> when): {matchId, playerId, winningTeamIndex, missionUpdates (the reporter's own)}
// The answer: the ranked payload the post-match screen reads (MvsRankedServerMatchPayload) from the reporter's rating in
// the match's mode, made when missing (EloRatings), and their character (connections:{id}, else Wonder Woman); with no
// session or no match id, or when it fails, {body: {}}.
//
// Unlike there:
//   - the winner is decided from every report, not the first (his rule; MatchWinner), and the set score follows it.
//   - the match's stats are recorded by the match flow from a report that agrees with that winner, once every human has
//     reported or 30 s after the first (TS: at once, from the first report, a spectator's included: TS-FINDINGS 4).
//   - the result reaches the match flow on a stream (TS: the pub/sub channel match:end_of_match_stats, lost when no
//     subscriber ran).
//   - Season is Season:Current (TS: always Season:SeasonFive), as ranked_data and the login.
//   - one rule for the mode (1v1 when it says 1v1): TS read the rating before the match by "2v2" and after by "1v1", so an
//     FFA match's RpDelta was elo_2v2 - elo_1v1.
//   - the rank TS looked up and never sent is not looked up.

public interface IMatchResults
{
    /// <summary>A report from <paramref name="playerId"/> (null: no session); the answer's body.</summary>
    Task<JsonObject> SubmitAsync(string? playerId, JsonObject request, CancellationToken ct);
}

internal sealed class MatchResults(IServiceProvider services, IRankedSets sets, EloRatings ratings, IOptionsMonitor<SeasonSettings> season,
    TimeProvider time, ILogger<MatchResults> log) : IMatchResults
{
    public const string Stream = "match:results";
    public const string DueKey = "match_results:due";
    public static readonly TimeSpan StatsWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(10);

    public static string ReportsKey(string matchId) => $"match_results:{matchId}";

    public async Task<JsonObject> SubmitAsync(string? playerId, JsonObject request, CancellationToken ct)
    {
        string? matchId = request["ContainerMatchId"] is JsonValue m && m.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        var stats = request["EndOfMatchStats"] as JsonObject;
        int? claimed = MatchWinner.Claimed(stats);
        log.LogInformation("Received end of match stats for match {Match}, WinningTeamIndex: {Winner}", matchId, stats?["WinningTeamIndex"]?.ToJsonString());
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("End of match stats for match {Match} not recorded: this service has no Redis (REDIS)", matchId);
            return [];
        }

        if (matchId is null)
        {
            return [];
        }

        await redis.StringSetAsync($"game_result_received:{matchId}", "1", s_ttl, When.NotExists);
        var config = Js.Parse((string?)await redis.StringGetAsync(matchId) ?? "null") as JsonObject;

        if (playerId is not null)
        {
            int? winner = await ReportAsync(redis, matchId, playerId, config, claimed, stats);
            // A set game is one RatedMatches counts (decided 2026-10-05; TS: any config without isCustomGame, so a rift's
            // winner opened a set of its own).
            if (winner is { } decided && config is not null && Leaderboards.RatedMatches.WhyNotRated(config["mode"] is JsonValue modeValue && modeValue.TryGetValue(out string? mode) ? mode : null,
                    config["players"] as JsonArray, Js.Parse((string?)await redis.StringGetAsync($"match:{matchId}") ?? "null") as JsonObject, config) is null)
            {
                var players = (config?["players"] as JsonArray ?? []).Where(p => !Truthy(p?["isSpectator"]))
                    .Select(p => p?["playerId"] is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>();
                await sets.RecordWinnerAsync([playerId, .. players.Where(p => p != playerId)], matchId, decided);
            }
        }

        await redis.StreamAddAsync(Stream, "result", Js.Stringify(new JsonObject
        {
            ["matchId"] = matchId,
            ["playerId"] = playerId,
            ["winningTeamIndex"] = claimed,
            ["missionUpdates"] = playerId is null ? null : stats?["PlayerMissionUpdates"]?[playerId]?.DeepClone(),
        }), maxLength: 10_000, useApproximateMaxLength: true);

        if (playerId is null)
        {
            return [];
        }

        try
        {
            return await AnswerAsync(redis, playerId, config, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.LogError("Error building ranked match payload: {Error}", e.Message);
            return [];
        }
    }

    // Keeps the player's first report beside the others; the winner they decide, so far.
    private async Task<int?> ReportAsync(IDatabase redis, string matchId, string playerId, JsonObject? config, int? claimed, JsonObject? stats)
    {
        string key = ReportsKey(matchId);
        long order = await redis.HashIncrementAsync(key, "#order");
        bool spectator = (config?["players"] as JsonArray ?? []).Any(p => p?["playerId"] is JsonValue v && v.TryGetValue(out string? s) && s == playerId && Truthy(p["isSpectator"]));
        bool first = await redis.HashSetAsync(key, playerId, Js.Stringify(new JsonObject
        {
            ["order"] = order,
            ["spectator"] = spectator,
            ["winner"] = claimed,
            ["stats"] = stats?.DeepClone(),
        }), When.NotExists);
        await redis.KeyExpireAsync(key, s_ttl);
        if (!first)
        {
            log.LogInformation("Match {Match}: {Player} reported again; their first report stands", matchId, playerId);
        }

        // The match's stats wait for every human, at most this long after the first report.
        await redis.SortedSetAddAsync(DueKey, matchId, (time.GetUtcNow() + StatsWait).ToUnixTimeMilliseconds(), SortedSetWhen.NotExists);

        var reports = await ReportsAsync(redis, matchId);
        int? winner = MatchWinner.Resolve(reports);
        if (reports.Select(r => r.Winner).Where(w => w is not null).Distinct().Count() > 1)
        {
            log.LogWarning("Match {Match}: the reports disagree ({Reports}); decided winner: team {Winner}", matchId,
                string.Join(", ", reports.OrderBy(r => r.Order).Select(r => $"{r.PlayerId}{(r.Spectator ? " (spectator)" : "")} says {r.Winner?.ToString() ?? "none"}")), winner);
        }

        return winner;
    }

    /// <summary>The reports kept for a match (MatchWinner's input), with each one's stats.</summary>
    internal static async Task<List<MatchReport>> ReportsAsync(IDatabase redis, string matchId) =>
        [.. (await ReportEntriesAsync(redis, matchId)).Select(e => e.Report)];

    internal static async Task<List<(MatchReport Report, JsonObject? Stats)>> ReportEntriesAsync(IDatabase redis, string matchId)
    {
        var entries = new List<(MatchReport, JsonObject?)>();
        foreach (var field in await redis.HashGetAllAsync(ReportsKey(matchId)))
        {
            if (field.Name == "#order" || Js.Parse(field.Value.ToString()) is not JsonObject report)
            {
                continue;
            }

            entries.Add((new MatchReport(field.Name.ToString(), (long)(MatchWinner.Number(report["order"]) ?? 0), Truthy(report["spectator"]),
                MatchWinner.Number(report["winner"]) is { } w ? (int)w : null), report["stats"] as JsonObject));
        }

        return entries;
    }

    // MvsRankedServerMatchPayload, as the TS server built it (its exact UE property names, RINGOUTS included).
    private async Task<JsonObject> AnswerAsync(IDatabase redis, string playerId, JsonObject? config, CancellationToken ct)
    {
        string mode = config?["mode"] is JsonValue mv && mv.TryGetValue(out string? text) ? text : "1v1";
        bool is1v1 = mode == "1v1" || mode.Contains("1v1", StringComparison.Ordinal);
        string suffix = is1v1 ? "1v1" : "2v2";
        if (await ratings.GetOrCreateAsync(playerId, "", ct) is not { } rating)
        {
            return [];
        }

        // The rating does not change here (a set is rated at its end): the TS server's delta was this one, nearly always 0.
        double before = Value(rating, $"elo_{suffix}");
        var updated = await ratings.GetOrCreateAsync(playerId, "", ct) ?? rating;
        double elo = Value(updated, $"elo_{suffix}"), wins = Value(updated, $"wins_{suffix}"), losses = Value(updated, $"losses_{suffix}");
        double games = wins + losses;
        string character = (string?)await redis.HashGetAsync($"connections:{playerId}", "character") is { Length: > 0 } c ? c : "character_wonder_woman";
        return new JsonObject
        {
            ["Season"] = season.CurrentValue.Current,
            ["Mode"] = suffix,
            ["Character"] = character,
            ["TotalGamesPlayedForMode"] = games,
            ["TotalSetsPlayedForMode"] = wins + losses,
            ["StarDelta"] = 0,
            ["StarSources"] = new JsonObject(),
            ["PreviousGoldenStar"] = 0,
            ["NewGoldenStar"] = 0,
            ["RpDelta"] = elo - before,
            ["UpdatedCharacterRecord"] = new JsonObject
            {
                ["CurrentPoints"] = elo,
                ["MaxPoints"] = elo,
                ["GamesPlayed"] = games,
                ["SetsPlayed"] = wins + losses,
                ["Stars"] = 0,
                ["MaxStars"] = 0,
                ["GoldenStar"] = 0,
                ["Wins"] = wins,
                ["Losses"] = losses,
                ["DamageDealt"] = 0,
                ["DamageTaken"] = 0,
                ["RINGOUTS"] = 0,
                ["Deaths"] = 0,
            },
            ["UpdatedBestCharacterRecord"] = new JsonObject
            {
                ["CharacterSlug"] = character,
                ["Stars"] = 0,
                ["MaxStars"] = 0,
                ["CurrentPoints"] = elo,
                ["MaxPoints"] = elo,
                ["GamesPlayed"] = games,
                ["SetsPlayed"] = wins + losses,
            },
        };
    }

    private static double Value(MongoDB.Bson.BsonDocument doc, string field) => doc.GetValue(field, MongoDB.Bson.BsonNull.Value) switch
    {
        MongoDB.Bson.BsonInt32 i => i.Value,
        MongoDB.Bson.BsonInt64 l => l.Value,
        MongoDB.Bson.BsonDouble d => d.Value,
        _ => 0,
    };

    private static bool Truthy(JsonNode? value) => value is JsonValue v && v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.Number => MatchWinner.Number(v) is { } d && d != 0 && !double.IsNaN(d),
        System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length > 0,
        _ => false,
    };
}

public static class MatchResultsHosting
{
    /// <summary>submit_end_of_match_stats's work (and the set score, ratings and season it needs).</summary>
    public static WebApplicationBuilder AddMatchResults(this WebApplicationBuilder builder)
    {
        builder.AddRankedSets();
        builder.AddSetting<SeasonSettings>("Season");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IMatchResults, MatchResults>();
        return builder;
    }
}
