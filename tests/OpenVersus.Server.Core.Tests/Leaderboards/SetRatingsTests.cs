using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Leaderboards;

/// <summary>
/// The set rating arithmetic at its edges (<see cref="SetRatings"/>) and the one rating check (<see cref="RatedMatches"/>).
/// Parity with the TS server's processSetResult over whole sets is tools/matches/set_diff.mjs. Mongo, a database of its
/// own, dropped (OVS_TEST_MONGO).
/// </summary>
public sealed class SetRatingsTests : IAsyncLifetime
{
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_set_rating_tests";
    private IMongoClient? _mongo;
    private static string Id(int n) => $"00000000000000000011{n:D4}";
    private static readonly string A = Id(1), B = Id(2), C = Id(3), D = Id(4);

    public async Task InitializeAsync()
    {
        if (s_mongo is { Length: > 0 })
        {
            _mongo = new MongoClient(s_mongo);
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    public async Task DisposeAsync()
    {
        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private IMongoCollection<BsonDocument> Ratings => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("eloratings");
    private IMongoCollection<BsonDocument> Stats => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playerstats");

    private SetRatings Writer(RankedSettings? settings = null)
    {
        var services = new ServiceCollection().AddSingleton(_mongo!.GetDatabase(TestMongoDb)).BuildServiceProvider();
        var ranked = new TestOptions<RankedSettings>(settings ?? new RankedSettings());
        return new SetRatings(services, new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance), ranked, TimeProvider.System,
            NullLogger<SetRatings>.Instance);
    }

    private async Task RatingAsync(string id, int sets, BsonDocument? characters = null, int elo = 0, int streak = 0)
    {
        var doc = new BsonDocument
        {
            { "account_id", id }, { "username", "" }, { "elo_1v1", elo }, { "elo_2v2", 0 }, { "wins_1v1", sets }, { "losses_1v1", 0 },
            { "wins_2v2", 0 }, { "losses_2v2", 0 }, { "win_streak_1v1", streak }, { "win_streak_2v2", 0 }, { "updated_at", 0.0 }, { "__v", 0 },
        };
        if (characters is not null)
        {
            doc["characters_1v1"] = characters;
        }

        await Ratings.InsertOneAsync(doc);
    }

    private async Task<BsonDocument> Of(IMongoCollection<BsonDocument> collection, string id) => await collection.Find(new BsonDocument("account_id", id)).FirstAsync();

    // A document with its keys sorted, at every level: an update's new fields are stored in Mongo's order, not the update's.
    private static BsonDocument Sorted(BsonDocument doc) =>
        new(doc.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => new BsonElement(e.Name, e.Value is BsonDocument d ? Sorted(d) : e.Value)));

    private static Dictionary<string, string> Fighters(params (string Id, string Slug)[] pairs) => pairs.ToDictionary(p => p.Id, p => p.Slug);

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]
    [InlineData(-2.51, -3)]
    [InlineData(27.2, 27)]
    [InlineData(-0.4, 0)]
    // JavaScript's Math.round, which a loser's negative half decides: .NET's default (to even) and AwayFromZero differ.
    public void RoundingIsJavaScripts(double value, double rounded) => Assert.Equal(rounded, SetRatings.JsRound(value));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0.20)]
    [InlineData(4, 0.20)]
    [InlineData(5, 0.35)]
    [InlineData(6, 0.35)]
    [InlineData(7, 0.50)]
    [InlineData(12, 0.50)]
    public void TheStreakBonusSteps(double streak, double bonus) => Assert.Equal(bonus, SetRatings.StreakBonus(streak));

    [SkippableFact]
    // A player rated after their session went (no connections:{id}: this writer has no Redis at all) keeps the name of
    // their player record, which is what their session held; a rating made nameless at a match's start gets it.
    public async Task APlayerWithNoSessionIsRatedUnderTheirRecordsName()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await RatingAsync(A, sets: 0);
        await RatingAsync(B, sets: 0);
        await _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters")
            .InsertOneAsync(new BsonDocument { { "_id", ObjectId.Parse(A) }, { "name", "Player A" } });

        await Writer().RateAsync(new SetOutcome([A], [B], "1v1", 2, 0, 0, false, Fighters(), "m"), default);

        Assert.Equal("Player A", (await Of(Ratings, A))["username"].AsString);
        Assert.Equal("", (await Of(Ratings, B))["username"].AsString);
    }

    [SkippableTheory]
    // An established player's K: 32 in 1v1 whatever Ranked:K2v2 says, Ranked:K2v2 in 2v2 (32 unless set).
    [InlineData("1v1", null, 16)]
    [InlineData("1v1", 24.0, 16)]
    [InlineData("2v2", null, 16)]
    [InlineData("2v2", 24.0, 12)]
    public async Task AnEstablishedPlayersKIs32In1v1AndRankedK2v2In2v2(string mode, double? k2v2, int gain)
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        // 25 sets in the mode each (no longer provisional), even mode ratings and no character: expected 0.5, a 2-0.
        foreach (string id in new[] { A, B, C, D })
        {
            await Ratings.InsertOneAsync(new BsonDocument
            {
                { "account_id", id }, { "username", "" }, { "elo_1v1", 500 }, { "elo_2v2", 500 }, { "wins_1v1", 25 }, { "losses_1v1", 0 },
                { "wins_2v2", 25 }, { "losses_2v2", 0 }, { "win_streak_1v1", 0 }, { "win_streak_2v2", 0 }, { "updated_at", 0.0 }, { "__v", 0 },
            });
        }

        var settings = k2v2 is { } k ? new RankedSettings { K2v2 = k } : null;
        var outcome = mode == "1v1"
            ? new SetOutcome([A], [B], "1v1", 2, 0, 0, false, Fighters(), "m")
            : new SetOutcome([A, B], [C, D], "2v2", 2, 0, 0, false, Fighters(), "m");
        var deltas = await Writer(settings).RateAsync(outcome, default);

        Assert.Equal(gain, deltas[A]);
        Assert.Equal(-gain, deltas[outcome.LoserIds[0]]);
    }

    [SkippableFact]
    public async Task A2To1SetCountsAt85PercentAndTheStreakBonusStartsAt3()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        // A's jake is on a streak of 2: this win is the third (+20%). Even ratings: expected 0.5, K 64 (fewer than 20 sets).
        await RatingAsync(A, sets: 5, new BsonDocument("character_jake", new BsonDocument { { "elo", 100 }, { "wins", 5 }, { "losses", 0 }, { "streak", 2 } }), elo: 100);
        await RatingAsync(B, sets: 5, new BsonDocument("character_finn", new BsonDocument { { "elo", 100 }, { "wins", 0 }, { "losses", 5 }, { "streak", 0 } }), elo: 100);
        var deltas = await Writer().RateAsync(new SetOutcome([A], [B], "1v1", 2, 1, 0, false, Fighters((A, "character_jake"), (B, "character_finn")), "m"), default);

        // round(64 * 0.5 * 0.85 * 1.2) = round(32.64) = 33; round(64 * -0.5 * 0.85) = round(-27.2) = -27, capped at -24.
        Assert.Equal(33, deltas[A]);
        Assert.Equal(-24, deltas[B]);
        var a = await Of(Ratings, A);
        Assert.Equal(new BsonDocument { { "elo", 133 }, { "wins", 6 }, { "losses", 0 }, { "streak", 3 } }, a["characters_1v1"]["character_jake"]);
        Assert.Equal(3, a["win_streak_1v1"].AsInt32);
        Assert.Equal(6, a["wins_1v1"].AsInt32);
        Assert.IsType<BsonDouble>(a["updated_at"]);
        var b = await Of(Ratings, B);
        Assert.Equal(76, b["elo_1v1"].AsInt32);
        Assert.Equal(0, b["win_streak_1v1"].AsInt32);
    }

    [SkippableFact]
    public async Task TheModesRatingIsTheBestCharactersAndTheGainAndLossHaveFloors()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        // A is far above B: A's expected score is near 1 (the gain floors at 6), B's near 0 (the loss floors at -3).
        // A's other character (shaggy, 2000) stays the mode's rating. 20 sets: K 32.
        await RatingAsync(A, sets: 20, new BsonDocument
        {
            { "character_jake", new BsonDocument { { "elo", 1500 }, { "wins", 20 }, { "losses", 0 }, { "streak", 0 } } },
            { "character_shaggy", new BsonDocument { { "elo", 2000 }, { "wins", 0 }, { "losses", 0 }, { "streak", 0 } } },
        }, elo: 2000);
        await RatingAsync(B, sets: 20, elo: 10);
        var deltas = await Writer().RateAsync(new SetOutcome([A], [B], "1v1", 2, 0, 0, false, Fighters((A, "character_jake")), "m"), default);

        Assert.Equal(6, deltas[A]);
        Assert.Equal(-3, deltas[B]);
        var a = await Of(Ratings, A);
        Assert.Equal(1506, a["characters_1v1"]["character_jake"]["elo"].AsInt32);
        Assert.Equal(2000, a["elo_1v1"].AsInt32);
        // B has no character: the mode's rating itself, and no characters map is written.
        var b = await Of(Ratings, B);
        Assert.Equal(7, b["elo_1v1"].AsInt32);
        Assert.False(b.Contains("characters_1v1"));
    }

    [SkippableFact]
    public async Task SetStatsAreUpsertedWithMongoosesDefaultsAndTeammatesIn2v2()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await Writer().RateAsync(new SetOutcome([A, B], [C, D], "2v2", 2, 0, 0, false,
            Fighters((A, "character_jake"), (B, "character_finn"), (C, "character_taz")), "m", IsPregameDodge: true), default);

        var a = await Of(Stats, A);
        Assert.Equal(new BsonInt32(0), a["__v"]);
        Assert.Equal(new BsonArray(), a["recent_matches_1v1"]);
        Assert.Equal(new BsonArray(), a["recent_matches_2v2"]);
        Assert.Equal(new BsonDocument(), a["characters_1v1"]);
        Assert.Equal(new BsonDocument(), a["aggregate"]);
        // New ratings all round: expected 0.5, a tossup. The matchup is against the first opponent's character, the
        // teammate's against B's; D's character is unknown.
        Assert.Equal(Sorted(BsonDocument.Parse("""
            { "wins": 1, "dodgeWins": 1, "tossupWins": 1,
              "matchups": { "character_taz": { "wins": 1, "tossupWins": 1, "dodges": 1 } },
              "teammates": { "character_finn": { "wins": 1 } } }
            """)), Sorted(a["characters_2v2"]["character_jake"].AsBsonDocument));
        var d = await Of(Stats, D);
        Assert.Equal(Sorted(BsonDocument.Parse("""
            { "losses": 1, "dodgeLosses": 1, "tossupLosses": 1,
              "matchups": { "character_jake": { "losses": 1, "tossupLosses": 1 } },
              "teammates": { "character_taz": { "losses": 1 } } }
            """)), Sorted(d["characters_2v2"]["unknown"].AsBsonDocument));
    }

    private static FighterCredit Credit(string character, bool won, FighterElo elo) => new(character, won, elo);

    [Fact]
    // What a set does to each fighter a Random picker played in it: one entry per fighter, however many of its games.
    public void ARandomPickersFightersAreCreditedByTheGamesTheyPlayed()
    {
        static (string, bool?)[] Games(params (string, bool?)[] games) => games;
        // Won 2-0: both fighters the win and the rating.
        Assert.Equal([Credit("jake", true, FighterElo.Up), Credit("finn", true, FighterElo.Up)], SetCredits.Of(true, Games(("jake", true), ("finn", true))));
        // Won 2-1: the two that won the same; the one that lost its game a loss, its rating as it was.
        Assert.Equal([Credit("jake", true, FighterElo.Up), Credit("finn", false, FighterElo.Flat), Credit("taz", true, FighterElo.Up)],
            SetCredits.Of(true, Games(("jake", true), ("finn", false), ("taz", true))));
        // Lost 0-2: every fighter the loss and the rating loss.
        Assert.Equal([Credit("jake", false, FighterElo.Down), Credit("finn", false, FighterElo.Down)], SetCredits.Of(false, Games(("jake", false), ("finn", false))));
        // Lost 1-2: the fighter that won its game loses rating as the others, and its record shows the win.
        Assert.Equal([Credit("jake", true, FighterElo.Down), Credit("finn", false, FighterElo.Down), Credit("taz", false, FighterElo.Down)],
            SetCredits.Of(false, Games(("jake", true), ("finn", false), ("taz", false))));
        // Rolled twice, a win and a loss: once, as the set went.
        Assert.Equal([Credit("jake", true, FighterElo.Up), Credit("finn", true, FighterElo.Up)], SetCredits.Of(true, Games(("jake", true), ("jake", false), ("finn", true))));
        Assert.Equal([Credit("jake", false, FighterElo.Down), Credit("finn", false, FighterElo.Down)], SetCredits.Of(false, Games(("jake", true), ("jake", false), ("finn", false))));
        // Rolled twice and won both: still once.
        Assert.Equal([Credit("jake", true, FighterElo.Up)], SetCredits.Of(true, Games(("jake", true), ("jake", true))));
        // A game the set ended in before its result (a concede, a dodge) counts for its fighter as the set went.
        Assert.Equal([Credit("jake", false, FighterElo.Flat), Credit("finn", true, FighterElo.Up)], SetCredits.Of(true, Games(("jake", false), ("finn", null))));
        Assert.Equal([Credit("jake", true, FighterElo.Down), Credit("finn", false, FighterElo.Down)], SetCredits.Of(false, Games(("jake", true), ("finn", null))));
        // No fighter known: nothing but the Random rating moves.
        Assert.Empty(SetCredits.Of(true, Games(("", true))));
    }

    [SkippableFact]
    // A set played as Random, won 2-1: Random is the character rated (and what the other player is measured against);
    // each fighter played is written beside it by its own game, and the mode's rating is the best of them all.
    public async Task ASetWonAsRandomRatesRandomAndEachFighterPlayed()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await RatingAsync(A, sets: 5, new BsonDocument
        {
            { RandomFighter.Character, new BsonDocument { { "elo", 100 }, { "wins", 1 }, { "losses", 0 }, { "streak", 0 } } },
            { "character_jake", new BsonDocument { { "elo", 100 }, { "wins", 5 }, { "losses", 0 }, { "streak", 2 } } },
            { "character_taz", new BsonDocument { { "elo", 300 }, { "wins", 0 }, { "losses", 0 }, { "streak", 4 } } },
            { "character_shaggy", new BsonDocument { { "elo", 50 }, { "wins", 0 }, { "losses", 2 }, { "streak", 0 } } },
        }, elo: 300);
        await RatingAsync(B, sets: 20, new BsonDocument("character_finn", new BsonDocument { { "elo", 100 }, { "wins", 20 }, { "losses", 0 }, { "streak", 0 } }), elo: 100);

        var deltas = await Writer().RateAsync(new SetOutcome([A], [B], "1v1", 2, 1, 0, false, Fighters((A, "character_shaggy"), (B, "character_finn")), "m",
            RandomPickers: new Dictionary<string, IReadOnlyList<FighterCredit>>
            {
                [A] = [Credit("character_jake", true, FighterElo.Up), Credit("character_taz", false, FighterElo.Flat), Credit("character_shaggy", true, FighterElo.Up)],
            }), default);

        // Random, 100 against finn's 100: expected 0.5, K 64, a 2-1: round(64 * 0.5 * 0.85) = 27.
        Assert.Equal(27, deltas[A]);
        var a = await Of(Ratings, A);
        var characters = a["characters_1v1"].AsBsonDocument;
        Assert.Equal(new BsonDocument { { "elo", 127 }, { "wins", 2 }, { "losses", 0 }, { "streak", 1 } }, characters[RandomFighter.Character]);
        // jake won its game, its third in a row (+20%): round(27.2 * 1.2) = 33.
        Assert.Equal(new BsonDocument { { "elo", 133 }, { "wins", 6 }, { "losses", 0 }, { "streak", 3 } }, characters["character_jake"]);
        // taz lost its game in a set that was won: the loss, the streak gone, the rating as it was.
        Assert.Equal(new BsonDocument { { "elo", 300 }, { "wins", 0 }, { "losses", 1 }, { "streak", 0 } }, characters["character_taz"]);
        // shaggy won its game from 50: expected 1 / (1 + 10^(50/800)) = 0.464; round(64 * 0.536 * 0.85) = 29.
        Assert.Equal(new BsonDocument { { "elo", 79 }, { "wins", 1 }, { "losses", 2 }, { "streak", 1 } }, characters["character_shaggy"]);
        // The mode's: the best character's (taz), one set won, Random's streak.
        Assert.Equal(300, a["elo_1v1"].AsInt32);
        Assert.Equal(6, a["wins_1v1"].AsInt32);
        Assert.Equal(0, a["losses_1v1"].AsInt32);
        Assert.Equal(1, a["win_streak_1v1"].AsInt32);

        // B was up against Random's 100 (not the fighters' ratings: their average, 150, would cost 13; shaggy's 50, 15):
        // K 32, round(32 * -0.5 * 0.85) = -14.
        Assert.Equal(-14, deltas[B]);
        Assert.Equal(86, (await Of(Ratings, B))["characters_1v1"]["character_finn"]["elo"].AsInt32);

        // The set stats: Random's entry alone among A's characters, the set once (so the characters' counts still add up
        // to the sets played), and inside it each fighter by its own game and expected score: taz was expected to win
        // (0.64), a choke. The fighters' own entries are not touched. To B, A was Random.
        var stats = (await Of(Stats, A))["characters_1v1"].AsBsonDocument;
        Assert.Equal([RandomFighter.Character], stats.Names);
        Assert.Equal(Sorted(BsonDocument.Parse("""
            { "wins": 1, "tossupWins": 1, "matchups": { "character_finn": { "wins": 1, "tossupWins": 1 } },
              "fighters": {
                "character_jake": { "wins": 1, "tossupWins": 1, "matchups": { "character_finn": { "wins": 1, "tossupWins": 1 } } },
                "character_taz": { "losses": 1, "chokes": 1, "matchups": { "character_finn": { "losses": 1, "chokes": 1 } } },
                "character_shaggy": { "wins": 1, "tossupWins": 1, "matchups": { "character_finn": { "wins": 1, "tossupWins": 1 } } } } }
            """)), Sorted(stats[RandomFighter.Character].AsBsonDocument));
        Assert.Equal(Sorted(BsonDocument.Parse($$"""{ "losses": 1, "tossupLosses": 1, "matchups": { "{{RandomFighter.Character}}": { "losses": 1, "tossupLosses": 1 } } }""")),
            Sorted((await Of(Stats, B))["characters_1v1"]["character_finn"].AsBsonDocument));
    }

    [SkippableFact]
    // A first set as Random, lost 1-2: Random starts from the default rating as any character does; every fighter loses
    // rating, once (taz played two games), and the one that won its game keeps the win on its record.
    public async Task ASetLostAsRandomCostsEveryFighterPlayedOnce()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await RatingAsync(C, sets: 0);
        await RatingAsync(D, sets: 0, new BsonDocument("character_jake", new BsonDocument { { "elo", 1000 }, { "wins", 0 }, { "losses", 0 }, { "streak", 0 } }), elo: 1000);

        var deltas = await Writer().RateAsync(new SetOutcome([D], [C], "1v1", 1, 2, 1, false, Fighters((C, "character_taz"), (D, "character_jake")), "m",
            RandomPickers: new Dictionary<string, IReadOnlyList<FighterCredit>>
            {
                [C] = SetCredits.Of(false, [("character_finn", true), ("character_taz", false), ("character_taz", false)]),
            }), default);

        // 1000 against 1000, K 64, a 2-1: round(-27.2) = -27, capped at -24; the winner gains 27.
        Assert.Equal(-24, deltas[C]);
        Assert.Equal(27, deltas[D]);
        var c = await Of(Ratings, C);
        Assert.Equal(Sorted(new BsonDocument
        {
            { RandomFighter.Character, new BsonDocument { { "elo", 976 }, { "wins", 0 }, { "losses", 1 }, { "streak", 0 } } },
            { "character_finn", new BsonDocument { { "elo", 976 }, { "wins", 1 }, { "losses", 0 }, { "streak", 0 } } },
            { "character_taz", new BsonDocument { { "elo", 976 }, { "wins", 0 }, { "losses", 1 }, { "streak", 0 } } },
        }), Sorted(c["characters_1v1"].AsBsonDocument));
        Assert.Equal(976, c["elo_1v1"].AsInt32);
        Assert.Equal(1, c["losses_1v1"].AsInt32);
        var stats = (await Of(Stats, C))["characters_1v1"].AsBsonDocument;
        Assert.Equal([RandomFighter.Character], stats.Names);
        Assert.Equal(1, stats[RandomFighter.Character]["losses"].AsInt32);
        var fighters = stats[RandomFighter.Character][SetRatings.RandomFightersField].AsBsonDocument;
        Assert.Equal(["character_finn", "character_taz"], fighters.Names.Order());
        Assert.Equal(1, fighters["character_finn"]["wins"].AsInt32);
        Assert.Equal(1, fighters["character_taz"]["losses"].AsInt32);
    }

    [SkippableFact]
    // A Random picker rated before any fighter of theirs is known: Random alone, in the rating and in the set stats.
    public async Task ARandomPickerWithNoFighterKnownIsRatedAsRandomAlone()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await Writer().RateAsync(new SetOutcome([A], [B], "1v1", 0, 0, 0, true, Fighters((A, RandomFighter.Character), (B, "character_finn")), "m",
            RandomPickers: new Dictionary<string, IReadOnlyList<FighterCredit>> { [A] = [] }), default);

        Assert.Equal([RandomFighter.Character], (await Of(Ratings, A))["characters_1v1"].AsBsonDocument.Names);
        var stats = (await Of(Stats, A))["characters_1v1"].AsBsonDocument;
        Assert.Equal([RandomFighter.Character], stats.Names);
        Assert.Equal(1, stats[RandomFighter.Character]["wins"].AsInt32);
        Assert.False(stats[RandomFighter.Character].AsBsonDocument.Contains(SetRatings.RandomFightersField));
        Assert.Equal([RandomFighter.Character], (await Of(Stats, B))["characters_1v1"]["character_finn"]["matchups"].AsBsonDocument.Names);
    }

    [Theory]
    [InlineData("1v1", false, null, null, null)]
    [InlineData("2v2", false, null, null, null)]
    [InlineData("ffa", false, null, null, "mode ffa is not rated")]
    [InlineData("1v1", true, null, null, "a bot played")]
    [InlineData("1v1", false, true, null, "a password match (custom lobby, rift or Casual)")]
    [InlineData("1v1", false, null, true, "a custom game")]
    public void OnlyRegularQueueMatchesWithNoBotsAreRated(string mode, bool bot, bool? password, bool? custom, string? why)
    {
        var players = new JsonArray(
            new JsonObject { ["playerId"] = A, ["teamIndex"] = 0, ["isBot"] = false },
            new JsonObject { ["playerId"] = B, ["teamIndex"] = 1, ["isBot"] = bot });
        var match = password is null ? null : new JsonObject { ["isPasswordMatch"] = password };
        var config = custom is null ? null : new JsonObject { ["isCustomGame"] = custom };
        Assert.Equal(why, RatedMatches.WhyNotRated(mode, players, match, config));
    }

    [Fact]
    public void ASetWithAnEmptyTeamIsNotRated() =>
        Assert.Equal("a team has no players", RatedMatches.WhyNotRated("1v1", [new JsonObject { ["playerId"] = A, ["teamIndex"] = 0 }]));
}
