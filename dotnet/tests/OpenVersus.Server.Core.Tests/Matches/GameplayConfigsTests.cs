using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The match configs (<see cref="IGameplayConfigs"/>) where the parity harness does not reach: what each mode writes, the
/// TTLs, a lock meeting a newer match's config or a player missing from Players, the bridge's switch, and the values
/// (stat trackers, tiers, map hazards) at their edges. Parity with the TS websocket over whole matches is
/// tools/matches/config_diff.mjs. Real Redis (database 15, OVS_TEST_REDIS) and Mongo (OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class GameplayConfigsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_gameplay_config_tests";
    private static string Id(int n) => $"00000000000000000018{n:D4}";
    private static readonly string Match = Id(100), Other = Id(101), P1 = Id(1), P2 = Id(2);

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        await CleanAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*00000000000000000018*"))
        {
            await Db.KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();
    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private IServiceProvider Services() => new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(Mongo).BuildServiceProvider();

    private GameplayConfigs Configs()
    {
        var services = Services();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings { DefaultElo = 1000 });
        return new GameplayConfigs(services, new CosmeticsService(services, NullLogger<CosmeticsService>.Instance),
            new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance), TimeProvider.System, NullLogger<GameplayConfigs>.Instance);
    }

    private static JsonObject Notification(string matchId) => new()
    {
        ["players"] = new JsonArray(
            new JsonObject { ["playerId"] = P1, ["partyId"] = matchId, ["playerIndex"] = 0, ["teamIndex"] = 0, ["isHost"] = true, ["ip"] = "198.51.100.1", ["isBot"] = false },
            new JsonObject { ["playerId"] = P2, ["partyId"] = matchId, ["playerIndex"] = 1, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "198.51.100.2", ["isBot"] = false }),
        ["matchId"] = matchId,
        ["map"] = "M001_V2",
        ["mode"] = "1v1",
    };

    // Both in session with a fighter; only P1 has a match copy of their cosmetics; neither has a rating.
    private async Task SeedAsync()
    {
        await Db.HashSetAsync($"connections:{P1}", [new("character", "character_jake"), new("skin", "skin_jake_default")]);
        await Db.HashSetAsync($"connections:{P2}", [new("character", "character_finn")]);
        await Db.HashSetAsync($"connections:{P1}:cosmetics", [new("Banner", "\"banner_one\"")]);
        await Db.StringSetAsync(Match, Js.Stringify(Notification(Match)), TimeSpan.FromMinutes(20));
    }

    private async Task<JsonObject> KeptAsync(string playerId) => (JsonObject)JsonNode.Parse((string)(await Db.StringGetAsync(GameplayConfigs.Key(playerId)))!)!;

    [SkippableFact]
    // Each player's config kept for the match's 20 minutes, and what the TS websocket wrote beside it: the fighters for the
    // set, a missing match copy of the cosmetics, a missing rating (made at 1000: Gold 1).
    public async Task TheBuildKeepsEachPlayersConfigAndWhatTheTsWebsocketWroteBesideIt()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();

        var message = await Configs().BuildAsync(Notification(Match), default);

        Assert.NotNull(message);
        foreach (string player in new[] { P1, P2 })
        {
            Assert.Equal(Js.Stringify(message), (string?)await Db.StringGetAsync(GameplayConfigs.Key(player)));
            Assert.InRange((await Db.KeyTimeToLiveAsync(GameplayConfigs.Key(player)))!.Value.TotalSeconds, 1190, 1200);
        }

        var p2 = message!["data"]!["GameplayConfig"]!["Players"]![P2]!;
        Assert.Equal("Gold", p2["RankedTier"]!.GetValue<string>());
        Assert.Equal(1, p2["RankedDivision"]!.GetValue<int>());
        Assert.Equal($"{{\"{P1}\":\"character_jake\",\"{P2}\":\"character_finn\"}}", (string?)await Db.StringGetAsync($"match_characters:{Match}"));
        Assert.True(await Db.KeyExistsAsync($"connections:{P2}:cosmetics"));
        var ratings = await Mongo.GetCollection<BsonDocument>("eloratings").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.Equal(2, ratings.Count);
        Assert.All(ratings, r => Assert.Equal("", r["username"].AsString));
    }

    [SkippableFact]
    public async Task ThePerksLockKeepsTheConfigsTtlAndMergesEveryLockedPlayer()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var configs = Configs();
        await configs.BuildAsync(Notification(Match), default);
        await Db.KeyExpireAsync(GameplayConfigs.Key(P1), TimeSpan.FromSeconds(100));
        await Db.StringSetAsync($"match:{Match}:perks:{P1}", "[\"perk_a\"]");
        await Db.StringSetAsync($"match:{Match}:perks:{P2}", "[]");

        await configs.PerksLockedAsync(new JsonObject { ["containerMatchId"] = Match, ["playerIds"] = new JsonArray(P1, P2) }, default);

        Assert.InRange((await Db.KeyTimeToLiveAsync(GameplayConfigs.Key(P1)))!.Value.TotalSeconds, 1, 100);
        foreach (string holder in new[] { P1, P2 })
        {
            var kept = await KeptAsync(holder);
            Assert.Equal(GameplayConfigs.PerksLockedTemplate, kept["data"]!["template_id"]!.GetValue<string>());
            Assert.Equal("[\"perk_a\"]", kept["data"]!["GameplayConfig"]!["Players"]![P1]!["Perks"]!.ToJsonString());
            Assert.Equal("[]", kept["data"]!["GameplayConfig"]!["Players"]![P2]!["Perks"]!.ToJsonString());
        }
    }

    // The copies come back to be sent in the TS websocket's order: the locked players' in the lock's order, then the
    // spectators'; what comes back is what is kept.
    [SkippableFact]
    public async Task TheLockGivesBackThePlayersCopiesInTheLocksOrderThenTheSpectators()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        string spectator = Id(5);
        var notification = Notification(Match);
        ((JsonArray)notification["players"]!).Add(new JsonObject { ["playerId"] = spectator, ["partyId"] = Match, ["playerIndex"] = 8888, ["teamIndex"] = -1, ["isHost"] = false, ["ip"] = "198.51.100.3", ["isBot"] = false, ["isSpectator"] = true });
        await Db.HashSetAsync($"connections:{spectator}", [new("character", "character_jake")]);
        await Db.StringSetAsync(Match, Js.Stringify(notification), TimeSpan.FromMinutes(20));
        var configs = Configs();
        await configs.BuildAsync(notification, default);
        await Db.StringSetAsync($"match:{Match}:perks:{P1}", "[\"perk_a\"]");
        await Db.StringSetAsync($"match:{Match}:perks:{P2}", "[]");

        var sent = await configs.PerksLockedAsync(new JsonObject { ["containerMatchId"] = Match, ["playerIds"] = new JsonArray(P2, P1) }, default);

        Assert.Equal([P2, P1, spectator], sent.Select(s => s.PlayerId));
        Assert.All(sent, s => Assert.Equal(GameplayConfigs.PerksLockedTemplate, s.Message["data"]!["template_id"]!.GetValue<string>()));
        foreach (var (id, message) in sent)
        {
            Assert.Equal(Js.Stringify(message), (string?)await Db.StringGetAsync(GameplayConfigs.Key(id)));
        }
    }

    [SkippableFact]
    public async Task ALockLeavesAPlayersNewerMatchConfigAlone()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var configs = Configs();
        await configs.BuildAsync(Notification(Other), default);
        string otherConfig = (string)(await Db.StringGetAsync(GameplayConfigs.Key(P2)))!;
        await configs.BuildAsync(Notification(Match), default);
        // P2 has moved on to the other match since.
        await Db.StringSetAsync(GameplayConfigs.Key(P2), otherConfig);
        await Db.StringSetAsync($"match:{Match}:perks:{P1}", "[\"perk_a\"]");
        await Db.StringSetAsync($"match:{Match}:perks:{P2}", "[\"perk_b\"]");

        await configs.PerksLockedAsync(new JsonObject { ["containerMatchId"] = Match, ["playerIds"] = new JsonArray(P1, P2) }, default);

        Assert.Equal(otherConfig, (string?)await Db.StringGetAsync(GameplayConfigs.Key(P2)));
        var p1 = await KeptAsync(P1);
        Assert.Equal(Match, p1["payload"]!["match"]!["id"]!.GetValue<string>());
        Assert.Equal("[\"perk_a\"]", p1["data"]!["GameplayConfig"]!["Players"]![P1]!["Perks"]!.ToJsonString());
        // P2 holds no copy of this match: as TS, whose lock merged P2's perks into the config P2's connection held.
        Assert.Equal("[]", p1["data"]!["GameplayConfig"]!["Players"]![P2]!["Perks"]!.ToJsonString());
    }

    [SkippableFact]
    public async Task ALockedPlayerMissingFromPlayersIsSkippedAndTheRestMerged()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var configs = Configs();
        await configs.BuildAsync(Notification(Match), default);
        foreach (string holder in new[] { P1, P2 })
        {
            var kept = await KeptAsync(holder);
            ((JsonObject)kept["data"]!["GameplayConfig"]!["Players"]!).Remove(P2);
            await Db.StringSetAsync(GameplayConfigs.Key(holder), Js.Stringify(kept), TimeSpan.FromMinutes(20));
        }

        await Db.StringSetAsync($"match:{Match}:perks:{P1}", "[\"perk_a\"]");
        await Db.StringSetAsync($"match:{Match}:perks:{P2}", "[\"perk_b\"]");

        await configs.PerksLockedAsync(new JsonObject { ["containerMatchId"] = Match, ["playerIds"] = new JsonArray(P1, P2) }, default);

        foreach (string holder in new[] { P1, P2 })
        {
            var kept = await KeptAsync(holder);
            Assert.Equal(GameplayConfigs.PerksLockedTemplate, kept["data"]!["template_id"]!.GetValue<string>());
            Assert.Equal("[\"perk_a\"]", kept["data"]!["GameplayConfig"]!["Players"]![P1]!["Perks"]!.ToJsonString());
        }
    }

    [SkippableFact]
    public async Task ABotPlaysWhatItsBotConfigSaysElseTheDefaults()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        string named = Id(3), plain = Id(4);
        await Db.HashSetAsync($"bot_config:{named}", [new("character", "character_arya"), new("difficultyMin", "hard"), new("difficultyMax", "")]);
        var notification = Notification(Match);
        foreach (string bot in new[] { named, plain })
        {
            notification["players"]!.AsArray().Add(new JsonObject { ["playerId"] = bot, ["partyId"] = Match, ["playerIndex"] = 2, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "", ["isBot"] = true });
        }

        var message = await Configs().BuildAsync(notification, default);

        var players = message!["data"]!["GameplayConfig"]!["Players"]!;
        // Number("hard") is NaN (null here), Number("") 0.
        Assert.Equal("character_arya", players[named]!["Character"]!.GetValue<string>());
        Assert.Null(players[named]!["BotDifficultyMin"]);
        Assert.Equal(0, players[named]!["BotDifficultyMax"]!.GetValue<long>());
        Assert.Equal(BotDefaults.Character, players[plain]!["Character"]!.GetValue<string>());
        Assert.Equal(BotDefaults.Skin, players[plain]!["Skin"]!.GetValue<string>());
        Assert.Equal(2, players[plain]!["BotDifficultyMin"]!.GetValue<long>());
        Assert.Equal(BotDefaults.PerksArray().ToJsonString(), players[plain]!["Perks"]!.ToJsonString());
        // A bot holds no config.
        Assert.False(await Db.KeyExistsAsync(GameplayConfigs.Key(plain)));
    }

    private static BsonDocument Stats(BsonDocument? oneVsOne = null, BsonDocument? twoVsTwo = null)
    {
        var doc = new BsonDocument("account_id", P1);
        if (oneVsOne is not null)
        {
            doc["characters_1v1"] = oneVsOne;
        }

        if (twoVsTwo is not null)
        {
            doc["characters_2v2"] = twoVsTwo;
        }

        return doc;
    }

    [Fact]
    public void AStatTrackerCountsTheFightersEntriesOfBothModes()
    {
        var stats = Stats(
            new BsonDocument("character_C025", new BsonDocument { { "wins", 397 }, { "highestDamageDealt", 621 }, { "totalDamageDealt", 10.25 } }),
            new BsonDocument
            {
                { "character_C025", new BsonDocument { { "wins", 5 }, { "highestDamageDealt", 281 }, { "totalDamageDealt", 0.25 } } },
                { "character_c025", new BsonDocument("wins", 2) },
                { "character_jake", new BsonDocument("wins", 1000) },
            });

        Assert.Equal(404, StatTrackerValues.Value("stattracking_c025wins", stats));
        Assert.Equal(621, StatTrackerValues.Value("stattracking_c025highestdamagedealt", stats));
        // 10.5, rounded as Math.round: up.
        Assert.Equal(11, StatTrackerValues.Value("stattracking_c025totaldamagedealt", stats));
    }

    [Fact]
    public void AStatTrackerFindsAFighterStoredUnderItsAlias()
    {
        var stats = Stats(twoVsTwo: new BsonDocument("character_creature", new BsonDocument("wins", 7)));

        Assert.Equal(7, StatTrackerValues.Value("stat_tracking_bundle_iron_giant_wins", stats));
    }

    [Fact]
    public void AStatTrackerWorthNothingOrUnreadable()
    {
        var stats = Stats(new BsonDocument { { "character_C025", BsonNull.Value }, { "character_jake", new BsonDocument("wins", "12") } });

        Assert.Equal(0, StatTrackerValues.Value(null, stats));
        Assert.Equal(0, StatTrackerValues.Value("", stats));
        Assert.Equal(0, StatTrackerValues.Value("stat_tracking_bundle_default", stats));
        Assert.Equal(0, StatTrackerValues.Value("stattracking_c025_emotes", stats));
        Assert.Equal(0, StatTrackerValues.Value("stattracking_c025wins", null));
        // Where TS threw: a slug that is not text, a stored entry of the fighter that is null.
        Assert.Throws<StatTrackerValues.UnreadableException>(() => StatTrackerValues.Value(5, stats));
        Assert.Throws<StatTrackerValues.UnreadableException>(() => StatTrackerValues.Value("stattracking_c025wins", stats));
    }

    [Theory]
    [InlineData(-1, "Bronze", 1)]
    [InlineData(0, "Bronze", 1)]
    [InlineData(499, "Bronze", 5)]
    [InlineData(499.5, "Bronze", 1)]
    [InlineData(500, "Silver", 1)]
    [InlineData(1234, "Gold", 3)]
    [InlineData(2999, "Master", 5)]
    [InlineData(3000, "Grandmaster", 1)]
    [InlineData(3550, "Grandmaster", 5)]
    [InlineData(double.NaN, "Bronze", 1)]
    public void ARatingsTierAndDivision(double elo, string tier, int division)
    {
        Assert.Equal((tier, division), RankedTiers.Of(elo));
    }

    [Theory]
    [InlineData("PVE_03", "1v1", true)]
    [InlineData("PVE_03", "2v2", true)]
    [InlineData("pve_03", "1v1", false)]
    [InlineData("M001_V2", "1v1", false)]
    [InlineData("m001_v2", "1v1", false)]
    [InlineData("unknown", "ffa", false)]
    public void AMapsHazards(string map, string mode, bool hazards)
    {
        Assert.Equal(hazards, MatchmakingMaps.Hazards(map, mode));
    }
}
