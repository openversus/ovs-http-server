using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// get_milestone_reward_tracks per player: everything starts unearned and nothing can be claimed; the fixed answer's
/// tracks and fixed fields are kept. Stored states: real Mongo, a database of its own, dropped (OVS_TEST_MONGO).
/// </summary>
public sealed class RewardTrackServiceTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_rewardtracks_tests";
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private IMongoClient? _mongo;

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(s_mongo))
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

    private RewardTrackService Service(bool perPlayer = true, bool mastery = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RewardTrackSettings>>(
            new TestOptions<RewardTrackSettings>(new RewardTrackSettings { PerPlayer = perPlayer, CharacterMastery = mastery }));
        if (_mongo is not null)
        {
            services.AddSingleton(_mongo.GetDatabase(TestMongoDb));
        }

        return new RewardTrackService(services.BuildServiceProvider(), NullLogger<RewardTrackService>.Instance);
    }

    private static JsonArray Fixed() => JsonNode.Parse(StaticResponses.Json("ssc-get-milestone-reward-tracks"))!["body"]!["RewardTrackStates"]!.AsArray();

    private static JsonArray States(JsonObject answer) => answer["body"]!["RewardTrackStates"]!.AsArray();

    private static JsonObject Track(JsonObject answer, string slug) =>
        States(answer).OfType<JsonObject>().Single(t => t["TrackSlug"]!.GetValue<string>() == slug);

    [Fact]
    public async Task NothingCanBeClaimedByAPlayerWhoEarnedNothing()
    {
        var answer = await Service().AnswerAsync(ObjectId.GenerateNewId().ToString(), default);
        foreach (var track in States(answer).OfType<JsonObject>())
        {
            Assert.Equal(0, track["CurrentScore"]!.GetValue<int>());
            // Every completed tier's rewards are claimed, but on End Game's battle pass, whose tier 1 (at 0 XP) is a
            // reward to claim (RewardTrackService.ClaimableFromStart).
            if (RewardTrackService.ClaimableFromStart.Contains(track["TrackSlug"]!.GetValue<string>()))
            {
                Assert.Empty(track["ClaimedRewards"]!.AsArray());
                continue;
            }

            var claimed = track["ClaimedRewards"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet();
            var tiers = HissTables.Data("milestone-reward-tracks", track["TrackSlug"]!.GetValue<string>())?["Tiers"] as JsonArray ?? [];
            foreach (var guid in track["CompletedTiers"]!.AsArray().Select(t => t!.GetValue<string>()))
            {
                var tier = tiers.OfType<JsonObject>().Single(t => t["TierGuid"]!.GetValue<string>() == guid);
                Assert.Equal(0, tier["ScoreThreshold"]!.GetValue<double>());
                Assert.All((tier["Rewards"] as JsonArray ?? []).OfType<JsonObject>(), r => Assert.Contains(r["RewardGuid"]!.GetValue<string>(), claimed));
            }
        }
    }

    [Fact]
    public async Task TracksStartWhereWbCountedAScoreOfZero()
    {
        var answer = await Service().AnswerAsync("", default);
        // A battle pass: its threshold-0 tier is reached (CurrentTier 1), and claimed; End Game's leaves it to claim.
        var pass = Track(answer, "mrt_battlepass_season_five");
        Assert.Equal(1, pass["CurrentTier"]!.GetValue<int>());
        Assert.Equal(["5A44B3F9428A35AEE479AF923854E5DA"], pass["CompletedTiers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Empty(pass["ClaimedRewards"]!.AsArray());
        var older = Track(answer, "mrt_battlepass_season_four");
        Assert.NotEmpty(older["CompletedTiers"]!.AsArray());
        Assert.NotEmpty(older["ClaimedRewards"]!.AsArray());
        // A character level, the daily bonus track: nothing reached.
        foreach (string slug in new[] { "mrt_mastery_wonder_woman", "mrt_bonus_mission_new", "mrt_mastery_account" })
        {
            var track = Track(answer, slug);
            Assert.Equal(0, track["CurrentTier"]!.GetValue<int>());
            Assert.Empty(track["CompletedTiers"]!.AsArray());
            Assert.Empty(track["ClaimedRewards"]!.AsArray());
            Assert.Equal(-1, track["HighestClaimedInifiniteTier"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TheFixedAnswersTracksAndFixedFieldsAreKept()
    {
        var answer = await Service().AnswerAsync("", default);
        var expected = Fixed().OfType<JsonObject>().ToList();
        var actual = States(answer).OfType<JsonObject>().ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Select(kv => kv.Key), actual[i].Select(kv => kv.Key));
            foreach (string field in new[] { "TrackSlug", "RewardTrackClass", "bHasPremium", "Guid", "InfiniteTierThreshold" })
            {
                Assert.True(JsonNode.DeepEquals(expected[i][field], actual[i][field]), $"{expected[i]["TrackSlug"]}.{field}");
            }
        }

        Assert.Equal(["body", "metadata", "return_code"], answer.Select(kv => kv.Key));
    }

    [SkippableFact]
    public async Task AddedScoreReturnsTheChangedTracksAsTheAnswerListsThem()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        var changed = await Service().AddScoreAsync(id, new Dictionary<string, int> { ["mrt_bonus_mission_new"] = 1, ["mrt_battlepass_season_five"] = 2150 }, default);
        var answer = await Service().AnswerAsync(id, default);
        // In the answer's order (the battle pass comes first there), each entry equal to the answer's.
        var expected = States(answer).OfType<JsonObject>()
            .Where(t => t["TrackSlug"]!.GetValue<string>() is "mrt_bonus_mission_new" or "mrt_battlepass_season_five").ToList();
        Assert.Equal(expected.Select(e => e.ToJsonString()), changed.Select(c => c.ToJsonString()));
        Assert.Equal(2, Track(answer, "mrt_battlepass_season_five")["CurrentTier"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task ClaimingATrackClaimsTheRewardsOfItsCompletedTiersOnce()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        // Nothing earned: nothing to claim (the battle pass's free tier is claimed already).
        var (pass, none) = await Service().ClaimAllAsync(id, "mrt_battlepass_season_five", default);
        Assert.Empty(none);
        Assert.Equal(1, pass!["CurrentTier"]!.GetValue<int>());

        // One daily claimed: the bonus track's first tier (threshold 1) is completed, its reward claimable once.
        await Service().AddScoreAsync(id, new Dictionary<string, int> { ["mrt_bonus_mission_new"] = 1 }, default);
        var (bonus, claimed) = await Service().ClaimAllAsync(id, "mrt_bonus_mission_new", default);
        Assert.Equal(["B21C1BFB44663B2442338985077A5C34"], claimed.Select(r => r["RewardGuid"]!.GetValue<string>()));
        Assert.Equal(["B21C1BFB44663B2442338985077A5C34"], bonus!["ClaimedRewards"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(bonus.ToJsonString(), Track(await Service().AnswerAsync(id, default), "mrt_bonus_mission_new").ToJsonString());
        Assert.Empty((await Service().ClaimAllAsync(id, "mrt_bonus_mission_new", default)).Claimed);

        // A track the answer does not list.
        Assert.Null((await Service().ClaimAllAsync(id, "mrt_no_such_track", default)).Track);
    }

    [Fact]
    public async Task CharacterLevelsAndTheOtherTracksSwitchApart()
    {
        var fixedPass = Fixed().OfType<JsonObject>().Single(t => t["TrackSlug"]!.GetValue<string>() == "mrt_battlepass_season_five");
        var fixedWonderWoman = Fixed().OfType<JsonObject>().Single(t => t["TrackSlug"]!.GetValue<string>() == "mrt_mastery_wonder_woman");

        // Levels the player's own, the rest as before: Wonder Woman from zero, the battle pass the fixed answer's.
        var levels = await Service(perPlayer: false, mastery: true).AnswerAsync("", default);
        Assert.Equal(0, Track(levels, "mrt_mastery_wonder_woman")["CurrentScore"]!.GetValue<int>());
        Assert.Equal(fixedPass.ToJsonString(), Track(levels, "mrt_battlepass_season_five").ToJsonString());

        // The other way round.
        var passes = await Service(perPlayer: true, mastery: false).AnswerAsync("", default);
        Assert.Equal(fixedWonderWoman.ToJsonString(), Track(passes, "mrt_mastery_wonder_woman").ToJsonString());
        Assert.Equal(0, Track(passes, "mrt_battlepass_season_five")["CurrentScore"]!.GetValue<int>());

        // Both off: the fixed answer's tracks as they are.
        var neither = await Service(perPlayer: false, mastery: false).AnswerAsync("", default);
        Assert.Equal(Fixed().ToJsonString(), States(neither).ToJsonString());
    }

    [SkippableFact]
    public async Task NothingIsAddedToOrClaimedOnATrackThatIsNotThePlayersOwn()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        var changed = await Service(perPlayer: true, mastery: false).AddScoreAsync(id,
            new Dictionary<string, int> { ["mrt_mastery_wonder_woman"] = 150, ["mrt_battlepass_season_five"] = 150 }, default);
        Assert.Equal(["mrt_battlepass_season_five"], changed.Select(t => t["TrackSlug"]!.GetValue<string>()));
        Assert.Null((await Service(perPlayer: true, mastery: false).ClaimAllAsync(id, "mrt_mastery_wonder_woman", default)).Track);
        // Turned on later: Wonder Woman starts from zero (nothing was recorded while off).
        Assert.Equal(0, Track(await Service().AnswerAsync(id, default), "mrt_mastery_wonder_woman")["CurrentScore"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task AStoredStateWins()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        var id = ObjectId.GenerateNewId();
        await _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(RewardTrackService.Collection).InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "tracks", new BsonDocument("mrt_bonus_mission_new", new BsonDocument
                {
                    { "CurrentScore", 1 }, { "CurrentTier", 1 }, { "CompletedTiers", new BsonArray { "F3C19189452E45BD79559390AD0792A4" } },
                    { "ClaimedRewards", new BsonArray() }, { "HighestClaimedInifiniteTier", -1 },
                }) },
        });
        var answer = await Service().AnswerAsync(id.ToString(), default);
        Assert.Equal(1, Track(answer, "mrt_bonus_mission_new")["CurrentScore"]!.GetValue<int>());
        Assert.Equal(0, Track(answer, "mrt_mastery_taz")["CurrentScore"]!.GetValue<int>());
    }
}
