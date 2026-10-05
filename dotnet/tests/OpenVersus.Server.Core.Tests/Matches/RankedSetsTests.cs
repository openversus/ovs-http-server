using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// A ranked set between its games (<see cref="IRankedSets"/>): check-ins, the next game, the end of the set, concedes,
/// and where the port deliberately differs from the TS server. Parity with the TS server over whole scenarios is
/// tools/matches/set_diff.mjs. Real Redis, database 15 (OVS_TEST_REDIS), and Mongo (a database of its own, dropped:
/// OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RankedSetsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_ranked_set_tests";
    private static string Id(int n) => $"00000000000000000010{n:D4}";
    private static readonly string Set = Id(100), Game2 = Id(200), P1 = Id(1), P2 = Id(2);

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private readonly ConcurrentQueue<(string Channel, JsonObject Message)> _published = new();
    private readonly List<string> _games = [];

    private sealed class Launcher : IMatchLauncher
    {
        public List<string> Deployed { get; } = [];

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> RollbackPortAsync(IDatabase redis) => Task.FromResult<int?>(57001);
        public void DeployIfOnDemand(int port, string matchId) => Deployed.Add(matchId);
    }

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
        // Channels ignore the database: only this class's set counts.
        foreach (string channel in new[] { RankedSets.CheckinChannel, RankedSets.LeaverChannel, RankedSets.FullRankUpdateChannel, MatchLauncher.NotificationChannel })
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, m) =>
            {
                if (m.ToString().Contains(P1, StringComparison.Ordinal))
                {
                    _published.Enqueue((channel, (JsonObject)JsonNode.Parse(m.ToString())!));
                }
            });
        }
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
        if ((string?)await Db.StringGetAsync($"ranked_set_match:{Set}") is { } game)
        {
            _games.Add(game);
        }

        foreach (var key in server.Keys(15, "*00000000000000000010*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        foreach (string game2 in _games)
        {
            await Db.KeyDeleteAsync([new RedisKey(game2), new RedisKey($"match:{game2}"), new RedisKey($"match_to_set:{game2}")]);
        }

        await Db.KeyDeleteAsync("online_players");
    }

    private IDatabase Db => _redis!.GetDatabase();
    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private RankedSets Sets(Launcher? launcher = null, bool p2p = false)
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(Mongo).BuildServiceProvider();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings());
        var ratings = new SetRatings(services, new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance), ranked,
            TimeProvider.System, NullLogger<SetRatings>.Instance);
        return new RankedSets(services, launcher ?? new Launcher(), ratings, new TestOptions<RollbackSettings>(new RollbackSettings { P2P = p2p }),
            TimeProvider.System, NullLogger<RankedSets>.Instance);
    }

    private static JsonArray Players(bool bot = false) =>
    [
        new JsonObject { ["playerId"] = P1, ["partyId"] = Id(700), ["playerIndex"] = 0, ["teamIndex"] = 0, ["isHost"] = true, ["ip"] = "198.51.100.1", ["isBot"] = false },
        new JsonObject { ["playerId"] = P2, ["partyId"] = Id(701), ["playerIndex"] = 1, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "198.51.100.2", ["isBot"] = bot },
    ];

    /// <summary>A 1v1 set after <paramref name="gamesPlayed"/> games at <paramref name="team0"/>-<paramref name="team1"/>,
    /// as the TS websocket leaves it at a game's end, with game 1's match and config still there.</summary>
    private async Task SeedAsync(int gamesPlayed, int team0, int team1, bool bot = false, JsonObject? extra = null)
    {
        var set = new JsonObject
        {
            ["players"] = Players(bot),
            ["mode"] = "1v1",
            ["gamesPlayed"] = gamesPlayed,
            ["scores"] = new JsonArray(team0, team1),
            ["checkins"] = new JsonArray(),
        };
        foreach (var (key, value) in extra ?? [])
        {
            set[key] = value?.DeepClone();
        }

        await Db.StringSetAsync($"ranked_set:{Set}", Js.Stringify(set));
        await Db.StringSetAsync($"match:{Set}", Js.Stringify(new JsonObject { ["matchId"] = Set, ["status"] = "pending" }));
        await Db.StringSetAsync(Set, Js.Stringify(new JsonObject { ["players"] = Players(bot), ["matchId"] = Set, ["mode"] = "1v1" }));
        await Db.StringSetAsync($"match_characters:{Set}", Js.Stringify(new JsonObject { [P1] = "character_jake", [P2] = "character_finn" }));
        foreach (string id in new[] { P1, P2 })
        {
            await Db.StringSetAsync($"player_ranked_set:{id}", Set);
            await Db.HashSetAsync($"connections:{id}", [new HashEntry("username", $"name {id[^1]}")]);
        }
    }

    private async Task<List<(string Channel, JsonObject Message)>> PublishedAsync(int waitMs = 200)
    {
        await Task.Delay(waitMs);
        return [.. _published];
    }

    private async Task<BsonDocument?> RatingAsync(string id) =>
        await Mongo.GetCollection<BsonDocument>("eloratings").Find(new BsonDocument("account_id", id)).FirstOrDefaultAsync();

    private async Task<JsonObject?> JsonAsync(string key) => (string?)await Db.StringGetAsync(key) is { } raw ? JsonNode.Parse(raw) as JsonObject : null;

    private async Task AssertDroppedAsync()
    {
        foreach (string key in new[] { $"player_ranked_set:{P1}", $"player_ranked_set:{P2}", $"ranked_set:{Set}", $"ranked_set_checkins:{Set}", $"ranked_set_match:{Set}" })
        {
            Assert.False(await Db.KeyExistsAsync(key), key);
        }
    }

    [SkippableFact]
    public async Task WhenAllHaveCheckedInTheNextGameIsMadeWithTheSameTeams()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0);
        var launcher = new Launcher();
        var sets = Sets(launcher);

        await sets.CheckinAsync(P1, Set);
        var first = await PublishedAsync();
        var checkin = Assert.Single(first).Message;
        Assert.Single(checkin["checkins"]!.AsArray());
        Assert.Equal(2, checkin["totalPlayers"]!.GetValue<int>());
        Assert.False(await Db.KeyExistsAsync($"ranked_set_match:{Set}"));

        await sets.CheckinAsync(P2, Set);
        string game = (string?)await Db.StringGetAsync($"ranked_set_match:{Set}") ?? throw new Xunit.Sdk.XunitException("no next game");
        _games.Add(game);

        // The match is rated (no isPasswordMatch) and holds one ticket of both players, as createNextSetMatch wrote it.
        var match = (await JsonAsync($"match:{game}"))!;
        Assert.False(match.ContainsKey("isPasswordMatch"));
        Assert.Equal("1v1", match["matchType"]!.GetValue<string>());
        var ticket = match["tickets"]![0]!.AsObject();
        Assert.Equal(game, ticket["matchmakingRequestId"]!.GetValue<string>());
        Assert.Equal($$"""[{"id":"{{P1}}","skill":0,"region":"local","partyId":"{{game}}"},{"id":"{{P2}}","skill":0,"region":"local","partyId":"{{game}}"}]""",
            ticket["players"]!.ToJsonString());

        // The config: the set's players unchanged (indexes, teams, host), a P2P decision, the matchmaker's port.
        var config = (await JsonAsync(game))!;
        Assert.Equal(Players().ToJsonString(), config["players"]!.ToJsonString());
        Assert.Equal(57001, config["rollbackPort"]!.GetValue<int>());
        Assert.False(config["p2p"]!.GetValue<bool>());
        Assert.Equal([game], launcher.Deployed);

        var set = (await JsonAsync($"ranked_set:{Set}"))!;
        Assert.Empty(set["checkins"]!.AsArray());
        Assert.Equal(1, set["gamesPlayed"]!.GetValue<int>());
        Assert.False(await Db.KeyExistsAsync($"ranked_set_checkins:{Set}"));
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"player_ranked_set:{P2}"));
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"match_to_set:{game}"));
        // The set's keys outlive the longest game (TS: 10 min, which a game could outlast before the websocket wrote them again).
        foreach (string key in new[] { $"ranked_set:{Set}", $"player_ranked_set:{P1}", $"player_ranked_set:{P2}", $"match_to_set:{game}" })
        {
            Assert.InRange((await Db.KeyTimeToLiveAsync(key))!.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        }

        Assert.Contains(await PublishedAsync(), p => p.Channel == MatchLauncher.NotificationChannel && p.Message["matchId"]!.GetValue<string>() == game);
        Assert.Null(await RatingAsync(P1));
    }

    [SkippableFact]
    public async Task ANextGameRunsP2PWhenSwitchedOnAndNoServerIsDeployed()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 0, 1);
        var launcher = new Launcher();
        var sets = Sets(launcher, p2p: true);
        await sets.CheckinAsync(P1, Set);
        await sets.CheckinAsync(P2, Set);
        string game = (string)(await Db.StringGetAsync($"ranked_set_match:{Set}"))!;
        _games.Add(game);
        Assert.True((await JsonAsync(game))!["p2p"]!.GetValue<bool>());
        Assert.Empty(launcher.Deployed);
    }

    [SkippableFact]
    // The late absent of 2026-10-02: sent for game 1 after game 2 was made, TS counted it toward game 2.
    public async Task ACheckInForAnEarlierGameOfTheSetIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0);
        await Db.StringSetAsync($"ranked_set_match:{Set}", Game2);
        var sets = Sets();

        await sets.CheckinAsync(P1, Set);
        Assert.False(await Db.KeyExistsAsync($"ranked_set_checkins:{Set}"));
        Assert.Empty(await PublishedAsync());

        await sets.CheckinAsync(P1, Game2);
        Assert.Equal(1, await Db.SetLengthAsync($"ranked_set_checkins:{Set}"));
        // No ContainerMatchId at all counts, as there: the second check-in, so the set goes on to its next game.
        await sets.CheckinAsync(P2, null);
        string next = (string)(await Db.StringGetAsync($"ranked_set_match:{Set}"))!;
        _games.Add(next);
        Assert.NotEqual(Game2, next);
    }

    [SkippableFact]
    // The pointer to the current game outlived by the set (a long game 2): the check-ins count, as TS counted them.
    public async Task WithNoCurrentGameKnownACheckInForALaterGameCounts()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 2, 1, 1);
        await Db.StringSetAsync($"match_to_set:{Game2}", Set);
        var sets = Sets();
        await sets.CheckinAsync(P1, Game2);
        Assert.Equal(1, await Db.SetLengthAsync($"ranked_set_checkins:{Set}"));
        await sets.CheckinAsync(P2, Game2);
        _games.Add((string)(await Db.StringGetAsync($"ranked_set_match:{Set}"))!);
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"match_to_set:{_games[^1]}"));
    }

    [SkippableFact]
    // ... and the game it names is the current one when it is the set's: a crash flagged under it is still seen.
    public async Task WithNoCurrentGameKnownACrashInTheNamedGameStillDropsTheSet()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 2, 1, 1);
        await Db.StringSetAsync($"match_to_set:{Game2}", Set);
        await Db.StringSetAsync($"match_server_crash:{Game2}", "1");
        await Sets().CheckinAsync(P1, Game2);
        await AssertDroppedAsync();
    }

    [SkippableFact]
    // TS dropped a check-in that found the set's lock taken: with the other player's check-in holding it, nobody went on.
    public async Task ACheckInWaitsForTheSetLockAndTheSetGoesOn()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0);
        var sets = Sets();
        await sets.CheckinAsync(P1, Set);
        await Db.StringSetAsync($"ranked_set_lock:{Set}", "another request", TimeSpan.FromSeconds(10));

        var second = sets.CheckinAsync(P2, Set);
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.False(await Db.KeyExistsAsync($"ranked_set_match:{Set}"));

        await Db.KeyDeleteAsync($"ranked_set_lock:{Set}");
        await second;
        Assert.True(await Db.KeyExistsAsync($"ranked_set_match:{Set}"));
    }

    [SkippableFact]
    // match_server_crash is written under the game's id: TS read only the set's (game 1), so a crash in game 2 was rated.
    public async Task ACrashInTheCurrentGameDropsTheSetUnratedAndSendsEveryoneBack()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 2, 1, 1);
        await Db.StringSetAsync($"ranked_set_match:{Set}", Game2);
        await Db.StringSetAsync($"match_server_crash:{Game2}", "1");
        await Db.StringSetAsync($"ranked_disconnect:{P2}", "1");

        await Sets().CheckinAsync(P1, Game2);

        await AssertDroppedAsync();
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        var leaver = Assert.Single(await PublishedAsync(), p => p.Channel == RankedSets.LeaverChannel).Message;
        Assert.Equal($$"""{"playerIds":["{{P1}}","{{P2}}"],"leaverPlayerId":"{{P1}}","matchId":"{{Set}}"}""", leaver.ToJsonString());
        Assert.Null(await RatingAsync(P1));
    }

    [SkippableFact]
    public async Task ASetWonAtCheckInIsRatedThenEveryoneIsSentBackAfterTheAnswer()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 2, 0, 2);
        var sets = Sets();
        await sets.CheckinAsync(P1, Set);
        await sets.CheckinAsync(P2, Set);

        await AssertDroppedAsync();
        Assert.Equal("set_over", (string?)await Db.StringGetAsync($"elo_processed_set:{Set}"));
        // Team 1 won 2-0: two new players, K 64, expected 0.5. The winner gains 32, the loser loses 24 (the cap), from 0.
        var winner = (await RatingAsync(P2))!;
        Assert.Equal(new BsonInt32(32), winner["elo_1v1"]);
        Assert.Equal(new BsonDocument { { "elo", 32 }, { "wins", 1 }, { "losses", 0 }, { "streak", 1 } }, winner["characters_1v1"]["character_finn"]);
        var loser = (await RatingAsync(P1))!;
        Assert.Equal(new BsonInt32(0), loser["elo_1v1"]);
        Assert.Equal(1, loser["losses_1v1"].AsInt32);

        var now = await PublishedAsync(150);
        Assert.Contains(now, p => p.Channel == RankedSets.FullRankUpdateChannel);
        Assert.DoesNotContain(now, p => p.Channel == RankedSets.LeaverChannel);
        var later = await PublishedAsync(700);
        Assert.Equal(P1, Assert.Single(later, p => p.Channel == RankedSets.LeaverChannel).Message["leaverPlayerId"]!.GetValue<string>());
    }

    [SkippableFact]
    // The fighter is locked for the set: once game 1's match_characters has expired (20 min), the current game's has it.
    public async Task CharactersComeFromTheCurrentGameFirst()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 2, 1, 1);
        await Db.StringSetAsync($"ranked_set_match:{Set}", Game2);
        await Db.KeyDeleteAsync($"match_characters:{Set}");
        await Db.StringSetAsync($"match_characters:{Game2}", Js.Stringify(new JsonObject { [P1] = "character_jake", [P2] = "character_finn" }));
        await Db.HashSetAsync($"connections:{P2}", "character", "character_taz");
        await Sets().ConcedeAsync(P1);

        var winner = (await RatingAsync(P2))!;
        Assert.True(winner["characters_1v1"].AsBsonDocument.Contains("character_finn"));
        Assert.False(winner["characters_1v1"].AsBsonDocument.Contains("character_taz"));
    }

    [SkippableFact]
    public async Task AConcedeRatesTheOtherTeamAtFullValue()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 0, 1);
        await Sets().ConcedeAsync(P2);

        await AssertDroppedAsync();
        Assert.Equal("concede", (string?)await Db.StringGetAsync($"elo_processed_set:{Set}"));
        // A concede counts as a 2-0 (modifier 1.0) whatever the score.
        Assert.Equal(32, (await RatingAsync(P1))!["elo_1v1"].AsInt32);
        var published = await PublishedAsync();
        Assert.Contains(published, p => p.Channel == RankedSets.FullRankUpdateChannel);
        Assert.Equal(P2, Assert.Single(published, p => p.Channel == RankedSets.LeaverChannel).Message["leaverPlayerId"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task AnOpponentWhoDisconnectedAndIsOfflineConcedesAtTheNextCheckIn()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0);
        await Db.StringSetAsync($"ranked_disconnect:{P2}", Set);
        await Sets().CheckinAsync(P1, Set);

        await AssertDroppedAsync();
        Assert.Equal("disconnect_concede", (string?)await Db.StringGetAsync($"elo_processed_set:{Set}"));
        Assert.Equal(32, (await RatingAsync(P1))!["elo_1v1"].AsInt32);
        Assert.Equal(P2, Assert.Single(await PublishedAsync(), p => p.Channel == RankedSets.LeaverChannel).Message["leaverPlayerId"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task AnOpponentOnlineAgainOnlyLeftAStaleFlag()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0);
        await Db.StringSetAsync($"ranked_disconnect:{P2}", Set);
        await Db.SetAddAsync("online_players", P2);
        await Sets().CheckinAsync(P1, Set);

        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        Assert.True(await Db.KeyExistsAsync($"ranked_set:{Set}"));
        Assert.Equal(RankedSets.CheckinChannel, Assert.Single(await PublishedAsync()).Channel);
    }

    [SkippableFact]
    public async Task AFlagNamingAnotherSetIsStale()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // Left by an earlier set (or TS's "1" after any match): it never concedes this one, even with the player offline.
        await SeedAsync(gamesPlayed: 1, 1, 0);
        await Db.StringSetAsync($"ranked_disconnect:{P2}", Id(999));
        await Sets().CheckinAsync(P1, Set);

        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        Assert.True(await Db.KeyExistsAsync($"ranked_set:{Set}"));
        Assert.False(await Db.KeyExistsAsync($"elo_processed_set:{Set}"));
        Assert.Equal(RankedSets.CheckinChannel, Assert.Single(await PublishedAsync()).Channel);
    }

    [SkippableFact]
    // RatedMatches: a set with a bot in it changes nobody's rating (MIGRATION-BRIDGES.md 6); the set ends as any other.
    public async Task ASetWithABotIsNotRated()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0, bot: true);
        await Sets().ConcedeAsync(P2);

        await AssertDroppedAsync();
        Assert.Null(await RatingAsync(P1));
        Assert.Null(await RatingAsync(P2));
        Assert.Contains(await PublishedAsync(), p => p.Channel == RankedSets.LeaverChannel);
    }

    [SkippableFact]
    public async Task ASetMarkedConcededAtAGamesEndEndsAtTheNextCheckIns()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 1, 1, 0, extra: new JsonObject { ["conceded"] = true, ["concedingPlayer"] = P2 });
        var sets = Sets();
        await sets.CheckinAsync(P1, Set);
        await sets.CheckinAsync(P2, Set);

        await AssertDroppedAsync();
        Assert.Equal(32, (await RatingAsync(P1))!["elo_1v1"].AsInt32);
        Assert.Equal(P2, Assert.Single(await PublishedAsync(800), p => p.Channel == RankedSets.LeaverChannel).Message["leaverPlayerId"]!.GetValue<string>());
    }

    [SkippableFact]
    public async Task AFaceoffTimeoutDropsTheSetUnrated()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(gamesPlayed: 0, 0, 0);
        await Db.StringSetAsync($"ranked_disconnect:{P2}", "1");
        await Sets().FaceoffTimeoutAsync(P1);

        await AssertDroppedAsync();
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        Assert.Empty(await PublishedAsync());
        Assert.Null(await RatingAsync(P1));
    }

    [SkippableFact]
    public async Task ACheckInForASetThatIsGoneWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Db.StringSetAsync($"player_ranked_set:{P1}", Set);
        await Sets().CheckinAsync(P1, Set);
        Assert.False(await Db.KeyExistsAsync($"ranked_set_checkins:{Set}"));
        Assert.Empty(await PublishedAsync());
    }
}
