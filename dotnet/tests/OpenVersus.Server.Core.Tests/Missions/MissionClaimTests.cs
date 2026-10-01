using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Tests.Missions;

/// <summary>
/// claim_mission_rewards: finished missions leave, their points reach the container's reward tracks, nothing twice.
/// Real Mongo, a database of its own, dropped (OVS_TEST_MONGO).
/// </summary>
public sealed class MissionClaimTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_missionclaim_tests";
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private IMongoClient? _mongo;

    private sealed class Monitor(MissionSettings value) : IOptionsMonitor<MissionSettings>
    {
        public MissionSettings CurrentValue => value;

        public MissionSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<MissionSettings, string?> listener) => null;
    }

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

    private static JsonObject Mission(string slug, string guid, int progress) => new()
    {
        [slug] = new JsonObject
        {
            ["MissionObjectives"] = new JsonArray(new JsonObject
            {
                ["Slug"] = HissTables.Data("missions", slug)!["MvsMissionData"]!["MissionObjectives"]![0]!["ObjectivePtr"]!.GetValue<string>(),
                ["Progress"] = progress,
            }),
            ["MissionGuid"] = guid,
        },
    };

    // A player with a finished and an unfinished daily, and a character group of two (one finished).
    private async Task<(MissionService Service, string Player, IMongoDatabase Db)> SetUpAsync()
    {
        var db = _mongo!.GetDatabase(TestMongoDb);
        var services = new ServiceCollection().AddSingleton(db).AddLogging()
            .AddSingleton<IRewardTrackService, RewardTrackService>().AddSingleton<IRewardGrants, RewardGrants>().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var settings = new MissionSettings { Enabled = true, Containers = "miscon_battlepassdaily_s5, miscon_unlockable_c020" };
        var service = new MissionService(services, new Monitor(settings), TimeProvider.System, new MissionRandom(), NullLogger<MissionService>.Instance);
        string player = ObjectId.GenerateNewId().ToString();
        await service.GetOrCreateAsync(player, default);

        var serverData = new JsonObject
        {
            ["MissionControllerContainers"] = new JsonObject
            {
                ["miscon_battlepassdaily_s5"] = new JsonObject { ["MissionControllers"] = new JsonObject
                {
                    ["misctl_battlepass_daily_base_pve_new"] = new JsonObject
                    {
                        ["Missions"] = new JsonArray(Mission("mis_stats_dealalldamage_pve", "done", 400), Mission("mis_stats_dealalldamage_pve", "half", 200)),
                        ["UsedMissions"] = new JsonArray("mis_stats_dealalldamage_pve", "mis_stats_dealalldamage_pve"),
                    },
                } },
                ["miscon_unlockable_c020"] = new JsonObject { ["MissionControllers"] = new JsonObject
                {
                    ["misctl_unlockable_c020"] = new JsonObject
                    {
                        ["Missions"] = new JsonArray(new JsonObject
                        {
                            ["mis_ringout_c020"] = Mission("mis_ringout_c020", "rick", 99)["mis_ringout_c020"]!.DeepClone(),
                            ["mis_usetaunts_c020"] = Mission("mis_usetaunts_c020", "taunt", 0)["mis_usetaunts_c020"]!.DeepClone(),
                        }),
                        ["UsedMissions"] = new JsonArray("mis_ringout_c020", "mis_usetaunts_c020"),
                    },
                } },
            },
            ["ClaimLocks"] = new JsonObject(),
        };
        await db.GetCollection<BsonDocument>("missionobjects").UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(player)), new BsonDocument("$set", new BsonDocument("server_data", BsonDocument.Parse(Js.Stringify(serverData)))));
        return (service, player, db);
    }

    private static JsonObject Claim(string container, params (string Controller, string Guid, string Slug)[] missions) => new()
    {
        ["ContainerSlug"] = container,
        ["MissionsToClaim"] = new JsonArray(missions.Select(m => (JsonNode?)new JsonObject
        {
            ["MissionControllerSlug"] = m.Controller, ["MissionGuid"] = m.Guid, ["MissionSlug"] = m.Slug,
        }).ToArray()),
    };

    private static JsonArray Groups(JsonObject answer, string container, string controller) =>
        answer["body"]!["MissionControllerContainers"]![container]!["MissionControllers"]![controller]!["Missions"]!.AsArray();

    private static async Task<JsonObject> TrackAsync(MissionService _, IMongoDatabase db, string player, string slug)
    {
        var doc = await db.GetCollection<BsonDocument>(RewardTrackService.Collection).Find(new BsonDocument("_id", ObjectId.Parse(player))).FirstOrDefaultAsync();
        return JsonNode.Parse(doc!["tracks"][slug].AsBsonDocument.ToJson())!.AsObject();
    }

    [SkippableFact]
    public async Task OnlyFinishedMissionsAreClaimedAndTheirPointsReachTheTracks()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        var (service, player, db) = await SetUpAsync();
        var answer = await service.ClaimAsync(player, Claim("miscon_battlepassdaily_s5",
            ("misctl_battlepass_daily_base_pve_new", "done", "mis_stats_dealalldamage_pve"),
            ("misctl_battlepass_daily_base_pve_new", "half", "mis_stats_dealalldamage_pve")), default);

        // The answer is the server_data: the finished one gone, the half-done one kept.
        Assert.Equal(["MissionControllerContainers", "ClaimLocks"], answer["body"]!.AsObject().Select(kv => kv.Key));
        var groups = Groups(answer, "miscon_battlepassdaily_s5", "misctl_battlepass_daily_base_pve_new");
        Assert.Single(groups);
        Assert.Equal("half", groups[0]!["mis_stats_dealalldamage_pve"]!["MissionGuid"]!.GetValue<string>());

        // 150 battle pass XP (its ScoreContribution) and 1 on the daily bonus track, whose first tier (1) is now earned.
        var pass = await TrackAsync(service, db, player, "mrt_battlepass_season_five");
        Assert.Equal(150, pass["CurrentScore"]!.GetValue<int>());
        Assert.Equal(1, pass["CurrentTier"]!.GetValue<int>());
        var bonus = await TrackAsync(service, db, player, "mrt_bonus_mission_new");
        Assert.Equal(1, bonus["CurrentScore"]!.GetValue<int>());
        Assert.Equal(["F3C19189452E45BD79559390AD0792A4"], bonus["CompletedTiers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Empty(bonus["ClaimedRewards"]!.AsArray());

        // Claiming it again changes nothing.
        await service.ClaimAsync(player, Claim("miscon_battlepassdaily_s5", ("misctl_battlepass_daily_base_pve_new", "done", "mis_stats_dealalldamage_pve")), default);
        Assert.Equal(150, (await TrackAsync(service, db, player, "mrt_battlepass_season_five"))["CurrentScore"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task AWrongGuidOrAnUnfinishedMissionClaimsNothing()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        var (service, player, db) = await SetUpAsync();
        var answer = await service.ClaimAsync(player, Claim("miscon_battlepassdaily_s5",
            ("misctl_battlepass_daily_base_pve_new", "nope", "mis_stats_dealalldamage_pve"),
            ("misctl_battlepass_daily_base_pve_new", "half", "mis_stats_dealalldamage_pve")), default);
        Assert.Equal(2, Groups(answer, "miscon_battlepassdaily_s5", "misctl_battlepass_daily_base_pve_new").Count);
        Assert.Null(await db.GetCollection<BsonDocument>(RewardTrackService.Collection).Find(new BsonDocument("_id", ObjectId.Parse(player))).FirstOrDefaultAsync());
    }

    [SkippableFact]
    public async Task ACharacterMissionLeavesItsGroup()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        var (service, player, _) = await SetUpAsync();
        var answer = await service.ClaimAsync(player, Claim("miscon_unlockable_c020", ("misctl_unlockable_c020", "rick", "mis_ringout_c020")), default);
        var group = Groups(answer, "miscon_unlockable_c020", "misctl_unlockable_c020").Single()!.AsObject();
        Assert.Equal(["mis_usetaunts_c020"], group.Select(kv => kv.Key));
    }

    [SkippableFact]
    public async Task AFinishedMissionIsAnsweredClaimable()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        var (service, player, _) = await SetUpAsync();
        var whole = await service.GetOrCreateAsync(player, default);
        var groups = Groups(new JsonObject { ["body"] = whole["body"]!["server_data"]!.DeepClone() }, "miscon_battlepassdaily_s5", "misctl_battlepass_daily_base_pve_new");
        var done = groups[0]!["mis_stats_dealalldamage_pve"]!.AsObject();
        Assert.Equal(["MissionObjectives", "MissionGuid", "bIsClaimable"], done.Select(kv => kv.Key));
        Assert.True(done["bIsClaimable"]!.GetValue<bool>());
        Assert.False(groups[1]!["mis_stats_dealalldamage_pve"]!.AsObject().ContainsKey("bIsClaimable"));
    }

    [Fact]
    public void TheTracksGainEachClaimsScoreAndOnePerMission()
    {
        var points = MissionService.TrackPoints("miscon_battlepassweekly_s5", ["mis_ringout_pve_weekly", "mis_ringout_pvp_weekly"]);
        Assert.Equal(2, points["MRT_Bonus_Weekly_Mission"]);
        int score = (int)HissTables.Data("missions", "mis_ringout_pve_weekly")!["MvsMissionData"]!["ScoreContribution"]!.GetValue<double>()
            + (int)HissTables.Data("missions", "mis_ringout_pvp_weekly")!["MvsMissionData"]!["ScoreContribution"]!.GetValue<double>();
        Assert.Equal(score, points["mrt_battlepass_season_five"]);
        Assert.Empty(MissionService.TrackPoints("miscon_unlockable_c020", ["mis_ringout_c020"]));
    }
}
