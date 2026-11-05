using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Leaderboards;
using Xunit;

namespace OpenVersus.Server.Core.Tests.Leaderboards;

/// <summary>
/// The season's floor reset (<see cref="RankedFloorReset"/>): the rule on its own, then over an eloratings collection
/// (Mongo, a database of its own, dropped: OVS_TEST_MONGO).
/// </summary>
public sealed class RankedFloorResetTests
{
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_floor_reset_tests";

    [Theory]
    [InlineData(1499, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(999, 500)]
    [InlineData(500, 500)]
    [InlineData(1500, 1500)]
    [InlineData(2499, 2000)]
    [InlineData(2999, 2500)]
    [InlineData(3600, 3000)]
    public void ATiersFloorIsItsFirstRating(double elo, double floor) => Assert.Equal(floor, RankedFloorReset.FloorOf(elo));

    [Theory]
    [InlineData(499)]
    [InlineData(0)]
    [InlineData(-5)]
    public void BronzeHasNoFloor(double elo) => Assert.Null(RankedFloorReset.FloorOf(elo));

    [Theory]
    // Enough sets: the tier's floor.
    [InlineData(1499, 10, 1000)]
    [InlineData(1780, 40, 1500)]
    [InlineData(976, 10, 500)]
    // Fewer than the min sets: the default, whatever the tier.
    [InlineData(976, 1, 1000)]
    [InlineData(1064, 2, 1000)]
    [InlineData(1780, 9, 1000)]
    // Under 500: untouched either way.
    [InlineData(450, 30, 450)]
    [InlineData(450, 1, 450)]
    public void ARatingGoesToItsFloorOrTheDefaultOrStays(double elo, int sets, double after) =>
        Assert.Equal(after, RankedFloorReset.Reset(elo, sets, minSets: 10, defaultElo: 1000));

    [Fact]
    public void NotARatingStaysNotARating() => Assert.True(double.IsNaN(RankedFloorReset.Reset(double.NaN, 50, 10, 1000)));

    [Fact]
    public void MinSetsZeroFloorsEveryone() => Assert.Equal(500, RankedFloorReset.Reset(976, 0, minSets: 0, defaultElo: 1000));

    [SkippableFact]
    // Over a collection: each character on its own, the mode rating the best reset character's (or its own reset), the
    // counters kept, slugs as stored; a dry run counts the same and writes nothing.
    public async Task ResetsEveryCharacterAndTheModeRatingsAndKeepsTheCounters()
    {
        Skip.If(string.IsNullOrEmpty(s_mongo), "set OVS_TEST_MONGO to run");
        var client = new MongoClient(s_mongo);
        await client.DropDatabaseAsync(TestMongoDb);
        var ratings = client.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("eloratings");
        try
        {
            await ratings.InsertManyAsync(
            [
                // Gold 1 on Jason (floored to 1000), Platinum on BananaGuard (to 1500, the mode's new rating), one lost set on C030 (to the default).
                Rating("a", elo1v1: 1780, wins: 40, losses: 30, characters: new BsonDocument
                {
                    ["character_Jason"] = Character(1499, 20, 15),
                    ["character_BananaGuard"] = Character(1780, 18, 12),
                    ["character_C030"] = Character(976, 0, 1),
                }, elo2v2: 1300, wins2v2: 12, losses2v2: 4, characters2v2: new BsonDocument { ["character_jason"] = Character(1300, 8, 4) }),
                // One set, lost: everything to the default (already there for the mode: unchanged).
                Rating("b", elo1v1: 976, wins: 0, losses: 1, characters: new BsonDocument { ["character_shaggy"] = Character(976, 0, 1) }),
                // Bronze with many sets: untouched.
                Rating("c", elo1v1: 450, wins: 5, losses: 40, characters: new BsonDocument { ["character_shaggy"] = Character(450, 5, 40) }),
                // No character rated (sets rated on the mode alone): the mode's own reset. A 2v2 rating of 0 (the old floor): not a positive rating, left as is.
                Rating("d", elo1v1: 1250, wins: 11, losses: 9, characters: null, elo2v2: 0, wins2v2: 30, losses2v2: 30, characters2v2: null),
                // A rating that is not a number, and a character without one: left alone, not a crash.
                new BsonDocument { ["account_id"] = "e", ["elo_1v1"] = "oops", ["characters_1v1"] = new BsonDocument { ["character_x"] = new BsonDocument("wins", 3) } },
            ]);

            var dry = await RankedFloorReset.RunAsync(ratings, minSets: 10, defaultElo: 1000, apply: false, NullLogger.Instance, CancellationToken.None);
            Assert.False(dry.Applied);
            Assert.Equal((5L, 3L), (dry.Players, dry.PlayersChanged));
            var dry1v1 = dry.Modes.Single(m => m.Mode == "1v1");
            Assert.Equal(new Dictionary<string, long> { ["1000"] = 1, ["1500"] = 1 }, dry1v1.Floored);
            Assert.Equal((2L, 1L, 2L, 1L), (dry1v1.ToDefault, dry1v1.Unchanged, dry1v1.ModeFloored, dry1v1.ModeToDefault));
            Assert.Equal(1780, (await Of(ratings, "a"))["elo_1v1"].ToDouble());

            var done = await RankedFloorReset.RunAsync(ratings, minSets: 10, defaultElo: 1000, apply: true, NullLogger.Instance, CancellationToken.None);
            Assert.True(done.Applied);
            Assert.Equal(Summary(dry), Summary(done));

            var a = await Of(ratings, "a");
            Assert.Equal(1500, a["elo_1v1"].ToDouble());
            Assert.Equal((1000.0, 1500.0, 1000.0), (a["characters_1v1"]["character_Jason"]["elo"].ToDouble(), a["characters_1v1"]["character_BananaGuard"]["elo"].ToDouble(), a["characters_1v1"]["character_C030"]["elo"].ToDouble()));
            Assert.Equal((40, 30, 20, 15), (a["wins_1v1"].ToInt32(), a["losses_1v1"].ToInt32(), a["characters_1v1"]["character_Jason"]["wins"].ToInt32(), a["characters_1v1"]["character_Jason"]["losses"].ToInt32()));
            Assert.Equal((1000.0, 1000.0), (a["elo_2v2"].ToDouble(), a["characters_2v2"]["character_jason"]["elo"].ToDouble()));
            Assert.IsType<BsonInt32>(a["elo_1v1"]);

            var b = await Of(ratings, "b");
            Assert.Equal((1000.0, 1000.0), (b["elo_1v1"].ToDouble(), b["characters_1v1"]["character_shaggy"]["elo"].ToDouble()));

            var c = await Of(ratings, "c");
            Assert.Equal((450.0, 450.0), (c["elo_1v1"].ToDouble(), c["characters_1v1"]["character_shaggy"]["elo"].ToDouble()));

            var d = await Of(ratings, "d");
            Assert.Equal((1000.0, 0.0), (d["elo_1v1"].ToDouble(), d["elo_2v2"].ToDouble()));

            var e = await Of(ratings, "e");
            Assert.Equal("oops", e["elo_1v1"].AsString);
            Assert.False(e["characters_1v1"]["character_x"].AsBsonDocument.Contains("elo"));

            // Again: nothing left to change.
            var again = await RankedFloorReset.RunAsync(ratings, minSets: 10, defaultElo: 1000, apply: true, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, again.PlayersChanged);
        }
        finally
        {
            await client.DropDatabaseAsync(TestMongoDb);
        }
    }

    // The counts as text, to compare two runs (the records hold lists, which compare by reference).
    private static string Summary(RankedFloorResetView view) =>
        $"{view.Players}/{view.PlayersChanged} " + string.Join("; ", view.Modes.Select(m =>
            $"{m.Mode}: {string.Join(",", m.Floored.Select(f => $"{f.Key}={f.Value}"))} {m.ToDefault} {m.Unchanged} {m.ModeFloored} {m.ModeToDefault}"));

    private static BsonDocument Character(double elo, int wins, int losses) => new() { ["elo"] = EloRatings.JsNumber(elo), ["wins"] = wins, ["losses"] = losses, ["streak"] = 0 };

    private static BsonDocument Rating(string id, double elo1v1, int wins, int losses, BsonDocument? characters, double elo2v2 = 1000, int wins2v2 = 0, int losses2v2 = 0, BsonDocument? characters2v2 = null)
    {
        var doc = new BsonDocument
        {
            ["account_id"] = id, ["username"] = id, ["elo_1v1"] = EloRatings.JsNumber(elo1v1), ["elo_2v2"] = EloRatings.JsNumber(elo2v2),
            ["wins_1v1"] = wins, ["losses_1v1"] = losses, ["wins_2v2"] = wins2v2, ["losses_2v2"] = losses2v2, ["win_streak_1v1"] = 2, ["win_streak_2v2"] = 0, ["updated_at"] = 0.0, ["__v"] = 0,
        };
        if (characters is not null)
        {
            doc["characters_1v1"] = characters;
        }

        if (characters2v2 is not null)
        {
            doc["characters_2v2"] = characters2v2;
        }

        return doc;
    }

    private static async Task<BsonDocument> Of(IMongoCollection<BsonDocument> ratings, string id) => await ratings.Find(new BsonDocument("account_id", id)).FirstAsync();
}
