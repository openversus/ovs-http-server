using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Leaderboards;

// A ranked set's result written into the ratings: the TS server's processSetResult (services/eloService.ts) and the
// recordSetStats it starts (services/statsService.ts), branch infinity-war, value for value. Whether a set counts at all
// is decided before, in one place (RatedMatches); this only writes.
//
// The rating, per player: a character's rating when the player's character is known (characters_{mode}.{slug}), else
// the mode's; against the other team's average (each player's own rating for that character, else the default). K is 64
// for a player with fewer than 20 sets in the mode, then 32. A set 2-0 (or conceded) counts in full, a 2-1 at 0.85.
//   winner  delta = round(K (1 - expected) modifier (1 + streak bonus)), at least 6; the streak bonus is +20% from a
//           streak of 3, +35% from 5, +50% from 7 (the streak counted with this win)
//   loser   delta = round(K (0 - expected) modifier), between -24 and -3; the streak goes back to 0
//   the new rating never below 0; with a character, the mode's rating becomes the player's best character's.
// round is JavaScript's Math.round (a half goes up: -2.5 is -2), not .NET's.
//
// Mongo, written  eloratings: the insert for a player with none (EloRatings.GetOrCreateAsync); updateOne {account_id}
//                 {$set: {win_streak_{mode}, updated_at (ms, a double), characters_{mode}.{slug}: {elo, wins, losses,
//                 streak} (with a character), elo_{mode}}, $inc: {wins_{mode} or losses_{mode}: 1}}
//                 playerstats, per player: updateOne {account_id} (upsert) {$setOnInsert: the schema's defaults that
//                 mongoose adds (__v 0, recent_matches_1v1 [], recent_matches_2v2 [], the other mode's characters {},
//                 aggregate {}), $set: {updated_at}, $inc: characters_{mode}.{slug or "unknown"}.{wins or losses,
//                 dodgeWins or dodgeLosses (a pregame dodge), upsets, chokes, tossupWins or tossupLosses, and the same
//                 under matchups.{the first opponent's character} (dodges for the winner of a pregame dodge) and, in 2v2,
//                 teammates.{the teammate's character} (no dodges, no tossups)}: 1}. An upset is a win expected below
//                 0.44, a choke a loss expected above 0.56, a tossup anything in between.
// Redis, read     connections:{id} username (the rating's username)
//
// Unlike there: the set stats are written before this returns (TS started them and did not wait); a failure is logged
// per player either way. A per-character entry missing a number counts it as 0 (TS: NaN, written into the rating; none
// of the 4,105 entries in the prod copy of 2026-09-29 is missing one).

/// <summary>
/// A finished set to rate: the teams, the mode, the score (team 0, team 1), who won and how. For End Game's set XP
/// (RankedSetXpPayout): who quit, and for a pregame dodge the games its set finished before.
/// </summary>
public sealed record SetOutcome(
    IReadOnlyList<string> WinnerIds,
    IReadOnlyList<string> LoserIds,
    string Mode,
    int Team0Wins,
    int Team1Wins,
    int WinnerTeam,
    bool IsConcede,
    IReadOnlyDictionary<string, string> Characters,
    string MatchId,
    bool IsPregameDodge = false,
    IReadOnlyList<string>? QuitterIds = null,
    int GamesBeforeDodge = 0);

public interface ISetRatings
{
    /// <summary>Writes <paramref name="outcome"/> into the players' ratings and set stats; each player's rating change.</summary>
    Task<IReadOnlyDictionary<string, int>> RateAsync(SetOutcome outcome, CancellationToken ct);
}

internal sealed class SetRatings(IServiceProvider services, EloRatings ratings, IOptionsMonitor<RankedSettings> settings, TimeProvider time,
    ILogger<SetRatings> log) : ISetRatings
{
    private const int ProvisionalSets = 20;
    private const double TossupLow = 0.44;
    private const double TossupHigh = 0.56;

    public async Task<IReadOnlyDictionary<string, int>> RateAsync(SetOutcome outcome, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var collection = mongo.GetCollection<BsonDocument>("eloratings");
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        double defaultElo = settings.CurrentValue.DefaultElo;
        bool is1v1 = outcome.Mode == "1v1" || outcome.Mode.Contains("1v1", StringComparison.Ordinal);
        string mode = is1v1 ? "1v1" : "2v2";
        string eloField = $"elo_{mode}", winsField = $"wins_{mode}", lossesField = $"losses_{mode}", streakField = $"win_streak_{mode}", charsField = $"characters_{mode}";
        double modifier = outcome.IsConcede || (outcome.WinnerTeam == 0 ? outcome.Team1Wins : outcome.Team0Wins) == 0 ? 1.0 : 0.85;

        var winners = new List<BsonDocument>();
        foreach (string id in outcome.WinnerIds)
        {
            winners.Add(await ratings.GetOrCreateAsync(collection, id, await UsernameAsync(redis, id), ct));
        }

        var losers = new List<BsonDocument>();
        foreach (string id in outcome.LoserIds)
        {
            losers.Add(await ratings.GetOrCreateAsync(collection, id, await UsernameAsync(redis, id), ct));
        }

        string Slug(string id) => outcome.Characters.TryGetValue(id, out string? slug) ? slug : "";
        BsonDocument? CharData(BsonDocument rating, string slug) =>
            slug.Length > 0 && rating.GetValue(charsField, BsonNull.Value) is BsonDocument chars && chars.GetValue(slug, BsonNull.Value) is BsonDocument data ? data : null;
        double CharElo(BsonDocument rating, string id) =>
            Slug(id) is { Length: > 0 } slug ? (CharData(rating, slug) is { } data && data.GetValue("elo", BsonNull.Value) is var elo && !elo.IsBsonNull ? Number(elo) : defaultElo) : Number(rating, eloField);

        double avgWinnerElo = winners.Sum(r => CharElo(r, r["account_id"].AsString)) / winners.Count;
        double avgLoserElo = losers.Sum(r => CharElo(r, r["account_id"].AsString)) / losers.Count;
        var deltas = new Dictionary<string, int>();
        var expectedScores = new Dictionary<string, double>();
        string score = $"{outcome.Team0Wins},{outcome.Team1Wins}";

        foreach (var (rating, won) in winners.Select(r => (r, true)).Concat(losers.Select(r => (r, false))))
        {
            string id = rating["account_id"].AsString;
            string slug = Slug(id);
            var data = CharData(rating, slug);
            double charElo = data is null ? defaultElo : Number(data, "elo");
            double charWins = data is null ? 0 : Number(data, "wins");
            double charLosses = data is null ? 0 : Number(data, "losses");
            double playerElo = slug.Length > 0 ? charElo : Number(rating, eloField);
            double k = Number(rating, winsField) + Number(rating, lossesField) < ProvisionalSets ? 64 : 32;
            double expected = 1 / (1 + Math.Pow(10, ((won ? avgLoserElo : avgWinnerElo) - playerElo) / 800));
            expectedScores[id] = expected;

            double streak = 0;
            double delta;
            if (won)
            {
                streak = (slug.Length > 0 ? (data is null ? 0 : Number(data, "streak")) : Number(rating, streakField)) + 1;
                delta = Math.Max(6, JsRound(k * (1 - expected) * modifier * (1 + StreakBonus(streak))));
            }
            else
            {
                delta = Math.Max(-24, Math.Min(-3, JsRound(k * (0 - expected) * modifier)));
            }

            double newElo = Math.Max(0, playerElo + delta);
            deltas[id] = (int)delta;

            var set = new BsonDocument
            {
                { streakField, EloRatings.JsNumber(streak) },
                { "updated_at", (double)time.GetUtcNow().ToUnixTimeMilliseconds() },
            };
            if (slug.Length > 0)
            {
                set[$"{charsField}.{slug}"] = new BsonDocument
                {
                    { "elo", EloRatings.JsNumber(newElo) },
                    { "wins", EloRatings.JsNumber(won ? charWins + 1 : charWins) },
                    { "losses", EloRatings.JsNumber(won ? charLosses : charLosses + 1) },
                    { "streak", EloRatings.JsNumber(streak) },
                };
                // The mode's rating becomes the best character's, this one's new rating included.
                double best = newElo;
                if (rating.GetValue(charsField, BsonNull.Value) is BsonDocument chars)
                {
                    foreach (var other in chars.Where(e => e.Name != slug))
                    {
                        best = Math.Max(best, other.Value is BsonDocument o ? Number(o, "elo") : 0);
                    }
                }

                set[eloField] = EloRatings.JsNumber(best);
            }
            else
            {
                set[eloField] = EloRatings.JsNumber(newElo);
            }

            await collection.UpdateOneAsync(new BsonDocument("account_id", id),
                new BsonDocument { { "$set", set }, { "$inc", new BsonDocument(won ? winsField : lossesField, 1) } }, cancellationToken: ct);
            log.LogInformation("{Player} ({Username}) SET {Result} [{Character}]: {Old} → {New} ({Delta}) [K={K}, expected={Expected:F3}, set={Score}{Streak}, modifier={Modifier}]",
                id, rating.GetValue("username", "").ToString(), won ? "WIN" : "LOSS", slug.Length > 0 ? slug : "global", playerElo, newElo,
                won ? $"+{delta}" : delta.ToString(System.Globalization.CultureInfo.InvariantCulture), k, expected, score,
                won ? $", streak={streak}" : "", modifier);
        }

        log.LogInformation("Processed set result: winners=[{Winners}] losers=[{Losers}] score={Score} concede={Concede} (avg ELO: winners={WinnerAvg} vs losers={LoserAvg})",
            string.Join(",", outcome.WinnerIds), string.Join(",", outcome.LoserIds), score, outcome.IsConcede, JsRound(avgWinnerElo), JsRound(avgLoserElo));

        await RecordSetStatsAsync(mongo, outcome, is1v1, charsField, expectedScores, ct);
        // End Game's set XP, as the TS processSetResult awarded it once the set was rated.
        if (redis is not null && outcome.MatchId.Length > 0)
        {
            await RewardTracks.RankedSetXpPayout.PublishSetAsync(redis, outcome);
        }

        return deltas;
    }

    // recordSetStats: one upsert per player, run together; a failure is the player's alone.
    private async Task RecordSetStatsAsync(IMongoDatabase mongo, SetOutcome outcome, bool is1v1, string charsField,
        Dictionary<string, double> expectedScores, CancellationToken ct)
    {
        var stats = mongo.GetCollection<BsonDocument>("playerstats");
        string Character(string? id) => id is not null && outcome.Characters.TryGetValue(id, out string? slug) && slug.Length > 0 ? slug : "unknown";
        var all = outcome.WinnerIds.Concat(outcome.LoserIds).ToList();

        await Task.WhenAll(all.Select(async playerId =>
        {
            bool won = outcome.WinnerIds.Contains(playerId);
            string character = Character(playerId);
            double expected = expectedScores.TryGetValue(playerId, out double e) ? e : 0.5;
            var opponents = won ? outcome.LoserIds : outcome.WinnerIds;
            var teammates = (won ? outcome.WinnerIds : outcome.LoserIds).Where(id => id != playerId).ToList();
            string opponentCharacter = Character(opponents.FirstOrDefault());
            bool tossup = expected is >= TossupLow and <= TossupHigh;
            bool upset = won && expected < TossupLow;
            bool choke = !won && expected > TossupHigh;

            var inc = new BsonDocument();
            string path = $"{charsField}.{character}";
            inc[$"{path}.{(won ? "wins" : "losses")}"] = 1;
            if (outcome.IsPregameDodge)
            {
                inc[$"{path}.{(won ? "dodgeWins" : "dodgeLosses")}"] = 1;
            }

            if (upset)
            {
                inc[$"{path}.upsets"] = 1;
            }

            if (choke)
            {
                inc[$"{path}.chokes"] = 1;
            }

            if (tossup)
            {
                inc[$"{path}.{(won ? "tossupWins" : "tossupLosses")}"] = 1;
            }

            string matchup = $"{path}.matchups.{opponentCharacter}";
            inc[$"{matchup}.{(won ? "wins" : "losses")}"] = 1;
            if (upset)
            {
                inc[$"{matchup}.upsets"] = 1;
            }

            if (choke)
            {
                inc[$"{matchup}.chokes"] = 1;
            }

            if (tossup)
            {
                inc[$"{matchup}.{(won ? "tossupWins" : "tossupLosses")}"] = 1;
            }

            if (outcome.IsPregameDodge && won)
            {
                inc[$"{matchup}.dodges"] = 1;
            }

            if (!is1v1 && teammates.Count > 0)
            {
                string teammate = $"{path}.teammates.{Character(teammates[0])}";
                inc[$"{teammate}.{(won ? "wins" : "losses")}"] = 1;
                if (upset)
                {
                    inc[$"{teammate}.upsets"] = 1;
                }

                if (choke)
                {
                    inc[$"{teammate}.chokes"] = 1;
                }
            }

            // The defaults mongoose adds to an upsert ($setOnInsert), less the paths the update itself writes.
            var onInsert = new BsonDocument
            {
                { "__v", 0 },
                { "recent_matches_1v1", new BsonArray() },
                { "recent_matches_2v2", new BsonArray() },
                { is1v1 ? "characters_2v2" : "characters_1v1", new BsonDocument() },
                { "aggregate", new BsonDocument() },
            };
            try
            {
                await stats.UpdateOneAsync(new BsonDocument("account_id", playerId), new BsonDocument
                {
                    { "$setOnInsert", onInsert },
                    { "$set", new BsonDocument("updated_at", (double)time.GetUtcNow().ToUnixTimeMilliseconds()) },
                    { "$inc", inc },
                }, new UpdateOptions { IsUpsert = true }, ct);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                log.LogError("Failed to update set stats for {Player}: {Error}", playerId, ex.Message);
            }
        }));
        log.LogInformation("Recorded set stats for {Count} players, match {Match}", all.Count, outcome.MatchId);
    }

    private static async Task<string> UsernameAsync(IDatabase? redis, string playerId)
    {
        try
        {
            return redis is null ? "" : (string?)await redis.HashGetAsync($"connections:{playerId}", "username") ?? "";
        }
        catch (RedisException)
        {
            return "";
        }
    }

    /// <summary>The winner's bonus for a win streak of <paramref name="streak"/> (this win counted).</summary>
    internal static double StreakBonus(double streak) => streak >= 7 ? 0.50 : streak >= 5 ? 0.35 : streak >= 3 ? 0.20 : 0;

    /// <summary>JavaScript's Math.round: the nearest whole number, a half going up (-2.5 is -2).</summary>
    internal static double JsRound(double value)
    {
        double floor = Math.Floor(value);
        return value - floor >= 0.5 ? floor + 1 : floor;
    }

    // A stored number (a missing one is 0: mongoose's default for the rating's own fields).
    private static double Number(BsonDocument doc, string field) => Number(doc.GetValue(field, BsonNull.Value));

    private static double Number(BsonValue value) => value switch
    {
        BsonInt32 i => i.Value,
        BsonInt64 l => l.Value,
        BsonDouble d => d.Value,
        _ => 0,
    };
}

public static class SetRatingsHosting
{
    /// <summary>Set results into the ratings (and the ratings they need).</summary>
    public static WebApplicationBuilder AddSetRatings(this WebApplicationBuilder builder)
    {
        builder.AddEloRatings();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ISetRatings, SetRatings>();
        return builder;
    }
}
