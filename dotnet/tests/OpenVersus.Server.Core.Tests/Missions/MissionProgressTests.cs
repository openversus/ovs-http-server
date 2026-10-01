using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Missions;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Missions;

/// <summary>
/// Mission progress from a match: the rules against the real game data, and a result recorded end to end (real Redis,
/// database 15, and Mongo, a database of its own: OVS_TEST_REDIS[_USER/_PW], OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MissionProgressTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_missionprogress_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private IMongoClient? _mongo;
    private ConnectionMultiplexer? _redis;
    private readonly List<string> _keys = [];

    private static readonly List<string> s_daily = ["miscon_battlepassdaily_s5"];

    private sealed class Monitor(MissionSettings value) : IOptionsMonitor<MissionSettings>
    {
        public MissionSettings CurrentValue => value;

        public MissionSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<MissionSettings, string?> listener) => null;
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis) || string.IsNullOrEmpty(s_mongo))
        {
            return;
        }

        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            foreach (string key in _keys)
            {
                await _redis.GetDatabase().KeyDeleteAsync(key);
            }

            await _redis.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    // A daily container holding the given missions, one group each, at no progress.
    private static JsonObject ServerData(params (string Controller, string Mission)[] missions)
    {
        var controllers = new JsonObject();
        foreach (var (controller, mission) in missions)
        {
            var objectives = new JsonArray();
            foreach (var o in (HissTables.Data("missions", mission)!["MvsMissionData"]!["MissionObjectives"] as JsonArray)!.OfType<JsonObject>())
            {
                objectives.Add(new JsonObject { ["Slug"] = o["ObjectivePtr"]!.GetValue<string>(), ["Progress"] = 0 });
            }

            if (controllers[controller] is not JsonObject state)
            {
                state = new JsonObject { ["Missions"] = new JsonArray(), ["UsedMissions"] = new JsonArray() };
                controllers[controller] = state;
            }

            state["Missions"]!.AsArray().Add(new JsonObject { [mission] = new JsonObject { ["MissionObjectives"] = objectives, ["MissionGuid"] = "g" } });
        }

        return new JsonObject
        {
            ["MissionControllerContainers"] = new JsonObject { ["miscon_battlepassdaily_s5"] = new JsonObject { ["MissionControllers"] = controllers } },
            ["ClaimLocks"] = new JsonObject(),
        };
    }

    private static int Progress(JsonObject serverData, string controller, string mission) =>
        serverData["MissionControllerContainers"]!["miscon_battlepassdaily_s5"]!["MissionControllers"]![controller]!["Missions"]!.AsArray()
            .OfType<JsonObject>().Single(g => g.ContainsKey(mission))[mission]!["MissionObjectives"]![0]!["Progress"]!.GetValue<int>();

    // A rift match as the game reports one: Wonder Woman (a Tank), PvE, 1v1.
    private static MissionMatch Rift(double damage = 287.54, int ringouts = 2) => new(
        true, "character_wonder_woman", "skin_c001_s01", "1v1", "M001", false,
        new JsonObject
        {
            ["Stat:Game:Character:TotalDamageDealt"] = damage,
            ["Stat:Game:Character:TotalRingouts"] = ringouts,
            ["Objective:Match:Enemies:BuffApplied:Shocked"] = new JsonObject { ["_hydra_double"] = 0 },
        });

    [Fact]
    public void AMatchMovesTheMissionsItMeetsAndNoOthers()
    {
        var data = ServerData(
            ("misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"), // PvE damage, 400
            ("misctl_battlepass_daily_base_new", "mis_stats_dealalldamage_pvp"),     // PvP only
            ("misctl_battlepass_daily_new_2", "mis_ringout_2v2"),                    // 2v2 only
            ("misctl_battlepass_daily_new_1", "mis_totalupringouts_assassin"));      // Assassins only
        var unknown = new List<string>();
        Assert.True(MissionRules.Apply(data, s_daily, Rift(), unknown));
        Assert.Empty(unknown);

        Assert.Equal(287, Progress(data, "misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
        Assert.Equal(0, Progress(data, "misctl_battlepass_daily_base_new", "mis_stats_dealalldamage_pvp"));
        Assert.Equal(0, Progress(data, "misctl_battlepass_daily_new_2", "mis_ringout_2v2"));
        Assert.Equal(0, Progress(data, "misctl_battlepass_daily_new_1", "mis_totalupringouts_assassin"));

        // A second match adds up, to the mission's Count and no further.
        MissionRules.Apply(data, s_daily, Rift(), unknown);
        Assert.Equal(400, Progress(data, "misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
    }

    [Fact]
    public void ConditionsAreJudgedOnTheMatch()
    {
        // 2v2 PvP: the 2v2 ringouts move, the PvE damage does not.
        var data = ServerData(("misctl_battlepass_daily_new_2", "mis_ringout_2v2"), ("misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
        var pvp = Rift() with { Mode = "2v2", IsPvP = true };
        MissionRules.Apply(data, s_daily, pvp, []);
        Assert.Equal(2, Progress(data, "misctl_battlepass_daily_new_2", "mis_ringout_2v2"));
        Assert.Equal(0, Progress(data, "misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));

        // No damage dealt: nothing moves, and nothing changed is reported as such.
        var none = ServerData(("misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
        Assert.False(MissionRules.Apply(none, s_daily, Rift(damage: 0), []));
    }

    [Fact]
    public void ACharactersMissionsMoveOnlyWithThatCharacter()
    {
        var data = new JsonObject
        {
            ["MissionControllerContainers"] = new JsonObject
            {
                ["miscon_unlockable_c020"] = new JsonObject { ["MissionControllers"] = new JsonObject { ["misctl_unlockable_c020"] = new JsonObject
                {
                    ["Missions"] = new JsonArray(new JsonObject { ["mis_ringout_c020"] = new JsonObject
                    {
                        ["MissionObjectives"] = new JsonArray(new JsonObject { ["Slug"] = "misobj_ringout_any", ["Progress"] = 0 }), ["MissionGuid"] = "g",
                    } }),
                    ["UsedMissions"] = new JsonArray("mis_ringout_c020"),
                } } },
            },
        };
        List<string> live = ["miscon_unlockable_c020"];
        Assert.False(MissionRules.Apply(data, live, Rift(), []));
        Assert.True(MissionRules.Apply(data, live, Rift() with { Character = "character_C020", Skin = "" }, []));
        Assert.Equal(2, data["MissionControllerContainers"]!["miscon_unlockable_c020"]!["MissionControllers"]!["misctl_unlockable_c020"]!["Missions"]![0]!["mis_ringout_c020"]!["MissionObjectives"]![0]!["Progress"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task AResultIsRecordedOnceAndThePlayerIsTold()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var redis = _redis!.GetDatabase();
        string player = ObjectId.GenerateNewId().ToString(), match = ObjectId.GenerateNewId().ToString();
        _keys.AddRange([match, $"player:{player}", $"mission_match:{match}:{player}"]);
        var services = new ServiceCollection().AddSingleton(_mongo!.GetDatabase(TestMongoDb)).AddSingleton<IConnectionMultiplexer>(_redis).BuildServiceProvider();
        var settings = new MissionSettings { Enabled = true, Containers = "miscon_battlepassdaily_s5" };
        var service = new MissionService(services, new Monitor(settings), TimeProvider.System, new MissionRandom(), NullLogger<MissionService>.Instance);

        // The player's missions, with one PvE damage daily put in.
        await service.GetOrCreateAsync(player, default);
        var collection = _mongo.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("missionobjects");
        var doc = await collection.Find(new BsonDocument("_id", ObjectId.Parse(player))).FirstAsync();
        doc["server_data"] = BsonDocument.Parse(Js.Stringify(ServerData(("misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"))));
        await collection.ReplaceOneAsync(new BsonDocument("_id", doc["_id"]), doc);

        await redis.StringSetAsync(match, $$$"""{"players":[{"playerId":"{{{player}}}","teamIndex":0,"isBot":false},{"playerId":"Bot0","teamIndex":1,"isBot":true}],"matchId":"{{{match}}}","map":"M001","mode":"1v1","gameplayConfigOverride":{"bIsRift":true,"bIsPvP":false,"Map":"M001","ModeString":"1v1"}}""");
        await redis.HashSetAsync($"player:{player}", [new HashEntry("character", "character_wonder_woman"), new HashEntry("skin", "skin_c001_s01")]);
        var told = new TaskCompletionSource<string>();
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal("ws:send"), (_, m) =>
        {
            if (m.ToString().Contains(player, StringComparison.Ordinal))
            {
                told.TrySetResult(m.ToString());
            }
        });

        var counters = new JsonObject { ["Stat:Game:Character:TotalDamageDealt"] = 150.9 };
        await service.RecordMatchAsync(match, player, 0, counters, default);
        await service.RecordMatchAsync(match, player, 0, counters, default); // once per player and match

        var answer = await service.GetOrCreateAsync(player, default);
        Assert.Equal(150, Progress(answer["body"]!["server_data"]!.AsObject(), "misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
        var message = JsonNode.Parse(await told.Task.WaitAsync(TimeSpan.FromSeconds(5)))!["message"]!;
        Assert.Equal("profile-notification", message["cmd"]!.GetValue<string>());
        Assert.Equal("MissionUpdatesComplete", message["data"]!["template_id"]!.GetValue<string>());
        Assert.Equal(150, Progress(message["data"]!["data"]!["server_data"]!.AsObject(), "misctl_battlepass_daily_base_pve_new", "mis_stats_dealalldamage_pve"));
        Assert.EndsWith("Z", message["data"]!["data"]!["created_at"]!.GetValue<string>());
        await subscriber.UnsubscribeAllAsync();
    }
}
