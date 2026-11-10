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
// the mode's; against the other team's average (each player's own rating for that character, else the default), expected
// = 1 / (1 + 10^((theirs - mine) / Ranked:EloDivisor (800))). K is Ranked:KProvisional (64) for a player with fewer than
// Ranked:ProvisionalSets (20) sets in the mode, then Ranked:K1v1 or Ranked:K2v2 (32 both). A set 2-0 (or conceded) counts
// in full, a 2-1 at 0.85.
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
// Mongo, read     playertesters {_id} name: the username of a player whose session is gone (the same value: /access copies
//                 it into the session)
//
// A player who played the set as Random (SetOutcome.RandomPickers; the pick is locked for the set, the fighter is new
// each game: Matches/RandomFighter) is rated as Random, a character like any other (characters_{mode}.character_ovs_random:
// it takes the set's result once, counts for the mode's rating as their best may, and is what the other team is
// measured against). Each fighter they played in the set is written in the same update, once however many of its
// games it played (FighterCredit, decided by SetCredits), from its own rating against the same opponents, same K and
// modifier:
//   set won   a fighter that won a game: a win, and the winner's delta (its own streak);
//             one that only lost: a loss, its rating as it was, its streak 0
//   set lost  every fighter: the loser's delta; a loss, but a win for one that won every game it played
// A set that ends by a concede (a disconnect that concedes it and a dodge on the perk screen the same) is a 2-0 whatever
// its games were: every fighter the winners played the win and its delta, every fighter the conceding team played the
// loss and its delta, the fighter of the game it ended in included (a dodge is no way to roll again for free).
// The mode's own wins, losses and streak move once, as for anyone.
// In playerstats Random holds what was played as Random, apart from the fighters' own entries:
//   characters_{mode}.character_ovs_random                  the set's result, once, as any character's entry (with its
//                                                           matchup, teammate and dodge counters): the counts over
//                                                           the mode's characters still add up to the sets played
//   characters_{mode}.character_ovs_random.fighters.{slug}  each fighter played, by its own record and expected score
//                                                           (wins or losses, upsets, chokes, tossups, matchup, teammate)
// A fighter's own entry (characters_{mode}.{slug}) is what was played picking that fighter, and is not written here. To
// anyone else a Random picker is Random: their matchup and teammate entries are under character_ovs_random.
//
// Unlike there: the set stats are written before this returns (TS started them and did not wait); a failure is logged
// per player either way. A player rated after their session went (a dodge their own disconnect also cleans up after, a
// set that ends while they are away) keeps their name from their player record; TS rated them with none, and their
// leaderboard entry made with no name at their match's start stayed nameless. A per-character entry missing a number counts it as 0 (TS: NaN, written into the rating; none
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
    int GamesBeforeDodge = 0,
    IReadOnlyDictionary<string, IReadOnlyList<FighterCredit>>? RandomPickers = null);

/// <summary>What a set does to the rating of a fighter a Random picker played in it.</summary>
public enum FighterElo
{
    /// <summary>The winner's delta.</summary>
    Up,
    /// <summary>Nothing: the rating stays as it was.</summary>
    Flat,
    /// <summary>The loser's delta.</summary>
    Down,
}

/// <summary>A fighter a Random picker played in a set: what goes on its record (a win or a loss) and what its rating does.</summary>
public sealed record FighterCredit(string Character, bool Won, FighterElo Elo);

public static class SetCredits
{
    /// <summary>
    /// Each fighter of <paramref name="games"/> (one player's, in the order played; Won null for a game the set ended
    /// in before its result: it counts as the set went) once, in the order first played, for a set that player's team
    /// won (<paramref name="setWon"/>) or lost.
    /// </summary>
    public static IReadOnlyList<FighterCredit> Of(bool setWon, IEnumerable<(string Character, bool? Won)> games)
    {
        var credits = new List<FighterCredit>();
        foreach (var fighter in games.Where(g => g.Character.Length > 0).GroupBy(g => g.Character, g => g.Won ?? setWon))
        {
            credits.Add(setWon
                ? fighter.Any(won => won) ? new FighterCredit(fighter.Key, true, FighterElo.Up) : new FighterCredit(fighter.Key, false, FighterElo.Flat)
                : new FighterCredit(fighter.Key, fighter.All(won => won), FighterElo.Down));
        }

        return credits;
    }
}

public interface ISetRatings
{
    /// <summary>Writes <paramref name="outcome"/> into the players' ratings and set stats; each player's rating change.</summary>
    Task<IReadOnlyDictionary<string, int>> RateAsync(SetOutcome outcome, CancellationToken ct);
}

internal sealed class SetRatings(IServiceProvider services, EloRatings ratings, IOptionsMonitor<RankedSettings> settings, TimeProvider time,
    ILogger<SetRatings> log) : ISetRatings
{
    /// <summary>In playerstats, inside Random's entry: the fighters played as Random, each an entry of its own.</summary>
    public const string RandomFightersField = "fighters";

    private const double TossupLow = 0.44;
    private const double TossupHigh = 0.56;

    public async Task<IReadOnlyDictionary<string, int>> RateAsync(SetOutcome outcome, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var collection = mongo.GetCollection<BsonDocument>("eloratings");
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        var ranked = settings.CurrentValue;
        double defaultElo = ranked.DefaultElo;
        bool is1v1 = outcome.Mode == "1v1" || outcome.Mode.Contains("1v1", StringComparison.Ordinal);
        string mode = is1v1 ? "1v1" : "2v2";
        string eloField = $"elo_{mode}", winsField = $"wins_{mode}", lossesField = $"losses_{mode}", streakField = $"win_streak_{mode}", charsField = $"characters_{mode}";
        double modifier = outcome.IsConcede || (outcome.WinnerTeam == 0 ? outcome.Team1Wins : outcome.Team0Wins) == 0 ? 1.0 : 0.85;

        var winners = new List<BsonDocument>();
        foreach (string id in outcome.WinnerIds)
        {
            winners.Add(await ratings.GetOrCreateAsync(collection, id, await UsernameAsync(redis, mongo, id, ct), ct));
        }

        var losers = new List<BsonDocument>();
        foreach (string id in outcome.LoserIds)
        {
            losers.Add(await ratings.GetOrCreateAsync(collection, id, await UsernameAsync(redis, mongo, id, ct), ct));
        }

        // A Random picker's character is Random; the fighters they played are written beside it.
        IReadOnlyList<FighterCredit>? Fighters(string id) => outcome.RandomPickers is { } pickers && pickers.TryGetValue(id, out var fighters) ? fighters : null;
        string Slug(string id) => Fighters(id) is not null ? Matches.RandomFighter.Character : outcome.Characters.TryGetValue(id, out string? slug) ? slug : "";
        BsonDocument? CharData(BsonDocument rating, string slug) =>
            slug.Length > 0 && rating.GetValue(charsField, BsonNull.Value) is BsonDocument chars && chars.GetValue(slug, BsonNull.Value) is BsonDocument data ? data : null;
        double CharElo(BsonDocument rating, string id) =>
            Slug(id) is { Length: > 0 } slug ? (CharData(rating, slug) is { } data && data.GetValue("elo", BsonNull.Value) is var elo && !elo.IsBsonNull ? Number(elo) : defaultElo) : Number(rating, eloField);

        double avgWinnerElo = winners.Sum(r => CharElo(r, r["account_id"].AsString)) / winners.Count;
        double avgLoserElo = losers.Sum(r => CharElo(r, r["account_id"].AsString)) / losers.Count;
        var deltas = new Dictionary<string, int>();
        var expectedScores = new Dictionary<string, double>();
        var fighterExpected = new Dictionary<(string Player, string Character), double>();
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
            double k = Number(rating, winsField) + Number(rating, lossesField) < ranked.ProvisionalSets ? ranked.KProvisional : is1v1 ? ranked.K1v1 : ranked.K2v2;
            double expected = 1 / (1 + Math.Pow(10, ((won ? avgLoserElo : avgWinnerElo) - playerElo) / ranked.EloDivisor));
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
                var written = new Dictionary<string, double> { [slug] = newElo };
                foreach (var fighter in Fighters(id) ?? [])
                {
                    var was = CharData(rating, fighter.Character);
                    double fighterElo = was is null ? defaultElo : Number(was, "elo");
                    double fighterExpectedScore = 1 / (1 + Math.Pow(10, ((won ? avgLoserElo : avgWinnerElo) - fighterElo) / ranked.EloDivisor));
                    fighterExpected[(id, fighter.Character)] = fighterExpectedScore;
                    double fighterStreak = fighter.Elo == FighterElo.Up ? (was is null ? 0 : Number(was, "streak")) + 1 : 0;
                    double fighterDelta = fighter.Elo switch
                    {
                        FighterElo.Up => Math.Max(6, JsRound(k * (1 - fighterExpectedScore) * modifier * (1 + StreakBonus(fighterStreak)))),
                        FighterElo.Down => Math.Max(-24, Math.Min(-3, JsRound(k * (0 - fighterExpectedScore) * modifier))),
                        _ => 0,
                    };
                    written[fighter.Character] = Math.Max(0, fighterElo + fighterDelta);
                    set[$"{charsField}.{fighter.Character}"] = new BsonDocument
                    {
                        { "elo", EloRatings.JsNumber(written[fighter.Character]) },
                        { "wins", EloRatings.JsNumber((was is null ? 0 : Number(was, "wins")) + (fighter.Won ? 1 : 0)) },
                        { "losses", EloRatings.JsNumber((was is null ? 0 : Number(was, "losses")) + (fighter.Won ? 0 : 1)) },
                        { "streak", EloRatings.JsNumber(fighterStreak) },
                    };
                    log.LogInformation("{Player} ({Username}) SET {Result} as Random, played [{Character}] ({Record}): {Old} → {New} ({Delta}) [expected={Expected:F3}]",
                        id, rating.GetValue("username", "").ToString(), won ? "WIN" : "LOSS", fighter.Character, fighter.Won ? "a win" : "a loss", fighterElo,
                        written[fighter.Character], fighterDelta > 0 ? $"+{fighterDelta}" : fighterDelta.ToString(System.Globalization.CultureInfo.InvariantCulture), fighterExpectedScore);
                }

                // The mode's rating becomes the best character's, the new ratings written here included.
                double best = written.Values.Max();
                if (rating.GetValue(charsField, BsonNull.Value) is BsonDocument chars)
                {
                    foreach (var other in chars.Where(e => !written.ContainsKey(e.Name)))
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

        await RecordSetStatsAsync(mongo, outcome, is1v1, charsField, expectedScores, fighterExpected, ct);
        // End Game's set XP, as the TS processSetResult awarded it once the set was rated.
        if (redis is not null && outcome.MatchId.Length > 0)
        {
            await RewardTracks.RankedSetXpPayout.AppendSetAsync(redis, outcome);
        }

        return deltas;
    }

    // recordSetStats: one upsert per player, run together; a failure is the player's alone. A Random picker's is
    // Random's entry (the set's result) and, inside it, each fighter's (its own record and expected score).
    private async Task RecordSetStatsAsync(IMongoDatabase mongo, SetOutcome outcome, bool is1v1, string charsField,
        Dictionary<string, double> expectedScores, Dictionary<(string Player, string Character), double> fighterExpected, CancellationToken ct)
    {
        var stats = mongo.GetCollection<BsonDocument>("playerstats");
        // A player's character as the others' entries name it: Random for a Random picker, else their fighter.
        string Character(string? id) =>
            id is null ? "unknown"
            : outcome.RandomPickers is { } random && random.ContainsKey(id) ? Matches.RandomFighter.Character
            : outcome.Characters.TryGetValue(id, out string? slug) && slug.Length > 0 ? slug : "unknown";
        var all = outcome.WinnerIds.Concat(outcome.LoserIds).ToList();

        await Task.WhenAll(all.Select(async playerId =>
        {
            bool setWon = outcome.WinnerIds.Contains(playerId);
            var opponents = setWon ? outcome.LoserIds : outcome.WinnerIds;
            var teammates = (setWon ? outcome.WinnerIds : outcome.LoserIds).Where(id => id != playerId).ToList();
            string opponentCharacter = Character(opponents.FirstOrDefault());
            var fighters = outcome.RandomPickers is { } pickers && pickers.TryGetValue(playerId, out var credited) ? credited : null;
            // The player's own entry, and for a Random picker each fighter's inside it.
            var entries = new List<(string Path, bool Won, double Expected, bool Dodge)>
            {
                ($"{charsField}.{Character(playerId)}", setWon, expectedScores.TryGetValue(playerId, out double e) ? e : 0.5, outcome.IsPregameDodge),
            };
            entries.AddRange((fighters ?? []).Select(f => ($"{charsField}.{Matches.RandomFighter.Character}.{RandomFightersField}.{f.Character}", f.Won,
                fighterExpected.TryGetValue((playerId, f.Character), out double fe) ? fe : 0.5, false)));

            var inc = new BsonDocument();
            foreach (var (path, won, expected, dodge) in entries)
            {
                bool tossup = expected is >= TossupLow and <= TossupHigh;
                bool upset = won && expected < TossupLow;
                bool choke = !won && expected > TossupHigh;

                inc[$"{path}.{(won ? "wins" : "losses")}"] = 1;
                if (dodge)
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

                if (dodge && won)
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

    // The session's username, else the player record's name (a player whose session is gone).
    private static async Task<string> UsernameAsync(IDatabase? redis, IMongoDatabase mongo, string playerId, CancellationToken ct)
    {
        try
        {
            if (redis is not null && (string?)await redis.HashGetAsync($"connections:{playerId}", "username") is { Length: > 0 } username)
            {
                return username;
            }
        }
        catch (RedisException)
        {
        }

        if (!ObjectId.TryParse(playerId, out var id))
        {
            return "";
        }

        var record = await mongo.GetCollection<BsonDocument>(Access.PlayerRecord.Collection).Find(new BsonDocument("_id", id))
            .Project(new BsonDocument("name", 1)).FirstOrDefaultAsync(ct);
        return record?.GetValue("name", "") is BsonString name ? name.Value : "";
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
