using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Leaderboards;

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

    private SetRatings Writer()
    {
        var services = new ServiceCollection().AddSingleton(_mongo!.GetDatabase(TestMongoDb)).BuildServiceProvider();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings());
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
