using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Seasons;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>Who won a game when its reports disagree (<see cref="MatchWinner"/>; his rule, 2026-10-05).</summary>
public sealed class MatchWinnerTests
{
    private static MatchReport P(string id, long order, int? winner) => new(id, order, false, winner);
    private static MatchReport S(string id, long order, int? winner) => new(id, order, true, winner);

    [Fact]
    public void PlayersWhoAgreeDecideWhateverTheSpectatorsSay() => Assert.Equal(1, MatchWinner.Resolve([P("a", 1, 1), S("s", 2, 0), P("b", 3, 1)]));

    [Fact]
    public void PlayersWhoDisagreeAreDecidedByTheSpectators() => Assert.Equal(1, MatchWinner.Resolve([P("a", 1, 0), P("b", 2, 1), S("s", 3, 1)]));

    [Fact]
    public void SpectatorsWhoDisagreeGoByMajorityThenTheFirst()
    {
        Assert.Equal(0, MatchWinner.Resolve([P("a", 1, 0), P("b", 2, 1), S("s", 3, 1), S("t", 4, 0), S("u", 5, 0)]));
        Assert.Equal(1, MatchWinner.Resolve([P("a", 1, 0), P("b", 2, 1), S("s", 3, 1), S("t", 4, 0)]));
    }

    [Fact]
    // A ranked set has no spectators: the first player, as TS took the first report.
    public void WithNoSpectatorTheFirstPlayerDecides() => Assert.Equal(1, MatchWinner.Resolve([P("b", 2, 0), P("a", 1, 1)]));

    [Fact]
    public void OnlySpectatorsOrNoClaims()
    {
        Assert.Equal(0, MatchWinner.Resolve([S("s", 1, 0), P("a", 2, null)]));
        Assert.Null(MatchWinner.Resolve([P("a", 1, null)]));
    }

    [Theory]
    [InlineData("""{"WinningTeamIndex":1}""", 1)]
    [InlineData("""{"WinningTeamIndex":{"_hydra_double":0}}""", 0)]
    [InlineData("""{"WinningTeamIndex":0.5}""", null)]
    [InlineData("""{"WinningTeamIndex":"1"}""", null)]
    [InlineData("""{}""", null)]
    public void TheClaimedWinnerIsAWholeNumberEvenWhenSentAsADouble(string stats, int? winner) => Assert.Equal(winner, MatchWinner.Claimed(JsonNode.Parse(stats)));
}

/// <summary>
/// submit_end_of_match_stats (<see cref="IMatchResults"/>), the match flow's stream (<see cref="MatchResultStream"/>) and a
/// game's stats (<see cref="GameStats"/>). Real Redis, database 15 (OVS_TEST_REDIS), and Mongo (a database of its own,
/// dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchResultsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_match_result_tests";
    private static string Id(int n) => $"00000000000000000013{n:D4}";
    private static readonly string Match = Id(100), Set = Id(101), P1 = Id(1), P2 = Id(2), Spec = Id(3);

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private sealed class Launcher : IMatchLauncher
    {
        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> RollbackPortAsync(IDatabase redis) => Task.FromResult<int?>(57001);
        public void DeployIfOnDemand(int port, string matchId) { }
    }

    private sealed class Missions : IMissionService
    {
        public List<string> Recorded { get; } = [];
        public Task<JsonObject> GetOrCreateAsync(string accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> ClaimAsync(string accountId, JsonObject body, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordMatchXpAsync(string matchId, string playerId, int? winningTeamIndex, CancellationToken ct) { Recorded.Add($"xp {playerId} {winningTeamIndex}"); return Task.CompletedTask; }
        public Task RecordMatchAsync(string matchId, string playerId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct) { Recorded.Add($"missions {playerId}"); return Task.CompletedTask; }
    }

    private sealed class Rifts : IRiftProgressService
    {
        public int Results { get; set; }
        public bool Fail { get; set; }
        public Task<(JsonObject Dynamic, JsonObject Player)> InstanceAsync(string playerId, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordResultAsync(string matchId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct)
        {
            Results++;
            return Fail ? throw new InvalidOperationException("rift store down") : Task.CompletedTask;
        }
    }

    private sealed class Stats : IGameStats
    {
        public List<JsonObject> Recorded { get; } = [];
        public Task RecordAsync(string matchId, JsonObject endOfMatchStats, CancellationToken ct) { Recorded.Add(endOfMatchStats); return Task.CompletedTask; }
    }

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
        foreach (var key in server.Keys(15, "*00000000000000000013*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync([new RedisKey(MatchResults.Stream), new RedisKey(MatchResults.DueKey)]);
    }

    private IDatabase Db => _redis!.GetDatabase();
    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);
    private ServiceProvider Services() => new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(Mongo).BuildServiceProvider();

    private MatchResults Results()
    {
        var services = Services();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings());
        var elo = new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance);
        var season = new TestOptions<SeasonSettings>(new SeasonSettings { Current = "Season:SeasonSix" });
        var sets = new RankedSets(services, new Launcher(), new SetRatings(services, elo, ranked, TimeProvider.System, NullLogger<SetRatings>.Instance), elo,
            new TestOptions<RollbackSettings>(new RollbackSettings()), season, TimeProvider.System, NullLogger<RankedSets>.Instance);
        return new MatchResults(services, sets, elo, season, TimeProvider.System,
            NullLogger<MatchResults>.Instance);
    }

    private sealed class StatusEvents : IMatchStatusEvents
    {
        public List<(string Match, string Player)> Left { get; } = [];
        public Task<(int Status, JsonObject Answer)> HandleAsync(string? matchUpdateKey, JsonNode? body, string? from) => throw new NotSupportedException();
        public Task GameClosedAsync(string playerId, bool nodeGone = false) => throw new NotSupportedException();
        public Task LeftAsync(string matchId, string playerId) { Left.Add((matchId, playerId)); return Task.CompletedTask; }
    }

    private MatchResultStream Stream(Missions missions, Rifts rifts, Stats stats, StatusEvents? events = null) =>
        new(Services(), missions, new TestOptions<MissionSettings>(new MissionSettings { Enabled = true }), rifts, stats, events ?? new StatusEvents(), TimeProvider.System, NullLogger<MatchResultStream>.Instance);

    // A 1v1 (or a custom game with a spectator) as the matchmaker or lobby left it, with P1 and P2 in ranked set Set.
    private async Task SeedAsync(bool custom = false, bool spectator = false, bool set = true)
    {
        var players = new JsonArray(
            new JsonObject { ["playerId"] = P1, ["playerIndex"] = 0, ["teamIndex"] = 0, ["isBot"] = false },
            new JsonObject { ["playerId"] = P2, ["playerIndex"] = 1, ["teamIndex"] = 1, ["isBot"] = false });
        if (spectator)
        {
            players.Add(new JsonObject { ["playerId"] = Spec, ["playerIndex"] = 8888, ["teamIndex"] = -1, ["isSpectator"] = true });
        }

        var config = new JsonObject { ["players"] = players, ["matchId"] = Match, ["map"] = "M001", ["mode"] = "1v1" };
        if (custom)
        {
            config["isCustomGame"] = true;
        }

        await Db.StringSetAsync(Match, Js.Stringify(config));
        await Db.StringSetAsync($"match_characters:{Match}", Js.Stringify(new JsonObject { [P1] = "character_jake", [P2] = "character_finn" }));
        if (set)
        {
            await Db.StringSetAsync($"ranked_set:{Set}", Js.Stringify(new JsonObject { ["players"] = players.DeepClone(), ["mode"] = "1v1", ["gamesPlayed"] = 1, ["scores"] = new JsonArray(0, 0), ["checkins"] = new JsonArray() }));
            await Db.StringSetAsync($"player_ranked_set:{P1}", Set);
            await Db.StringSetAsync($"player_ranked_set:{P2}", Set);
        }
    }

    private static JsonObject Report(int? winner, int p1Ringouts = 2) => new()
    {
        ["ContainerMatchId"] = Match,
        ["EndOfMatchStats"] = new JsonObject
        {
            ["PlayerMissionUpdates"] = new JsonObject
            {
                [P1] = new JsonObject { ["Stat:Game:Character:TotalRingouts"] = p1Ringouts, ["Stat:Game:Character:TotalDamageDealt"] = 87.6 },
                [P2] = new JsonObject { ["Stat:Game:Character:TotalRingouts"] = 1, ["Stat:Game:Character:TotalAttackDamageDealt"] = 40 },
            },
            ["PlayerNetworkStats"] = new JsonObject { ["InputDelayFinal"] = 2 },
            ["Score"] = new JsonArray(3, 1),
            ["WinningTeamIndex"] = winner,
        },
        ["MatchLength"] = 4000,
    };

    private async Task<string> ScoresAsync() => Js.Stringify((Js.Parse((string)(await Db.StringGetAsync($"ranked_set:{Set}"))!) as JsonObject)!["scores"]);

    [SkippableFact]
    public async Task AReportIsKeptCountsTowardTheSetGoesOnTheStreamAndIsAnswered()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await Db.HashSetAsync($"connections:{P1}", "character", "character_jake");
        var answer = await Results().SubmitAsync(P1, Report(0), default);

        Assert.True(await Db.KeyExistsAsync($"game_result_received:{Match}"));
        Assert.True(await Db.HashExistsAsync(MatchResults.ReportsKey(Match), P1));
        Assert.NotNull(await Db.SortedSetScoreAsync(MatchResults.DueKey, Match));
        Assert.Equal("[1,0]", await ScoresAsync());
        var entry = Assert.Single(await Db.StreamRangeAsync(MatchResults.Stream));
        Assert.Equal($$$"""{"matchId":"{{{Match}}}","playerId":"{{{P1}}}","winningTeamIndex":0,"missionUpdates":{"Stat:Game:Character:TotalRingouts":2,"Stat:Game:Character:TotalDamageDealt":87.6}}""",
            (string?)entry["result"]);
        // Season:Current, not TS's Season Five; the rating made (from 0), the delta 0 (a set is rated at its end).
        Assert.Equal("Season:SeasonSix", answer["Season"]!.GetValue<string>());
        Assert.Equal("character_jake", answer["Character"]!.GetValue<string>());
        Assert.Equal(0, answer["RpDelta"]!.GetValue<double>());
        Assert.NotNull(await Mongo.GetCollection<BsonDocument>("eloratings").Find(new BsonDocument("account_id", P1)).FirstOrDefaultAsync());
    }

    [SkippableFact]
    public async Task AgreeingReportsCountOnceAndADisagreeingSecondPlayerDoesNotMoveThePoint()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var results = Results();
        await results.SubmitAsync(P1, Report(1), default);
        await results.SubmitAsync(P2, Report(1), default);
        Assert.Equal("[0,1]", await ScoresAsync());
        await results.SubmitAsync(P1, Report(0), default);
        Assert.Equal("[0,1]", await ScoresAsync());
    }

    [SkippableFact]
    // No spectator to break the tie: the first player's report stands, as TS took the first.
    public async Task PlayersWhoDisagreeWithNoSpectatorKeepTheFirstReport()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var results = Results();
        await results.SubmitAsync(P2, Report(1), default);
        await results.SubmitAsync(P1, Report(0), default);
        Assert.Equal("[0,1]", await ScoresAsync());
        // A player's first report stands: P2 changing its mind would make the two agree on team 0.
        await results.SubmitAsync(P2, Report(0), default);
        Assert.Equal("[0,1]", await ScoresAsync());
    }

    [SkippableFact]
    // The spectator's simulation is the definitive one: when the players disagree it decides, and the point moves.
    public async Task ASpectatorDecidesBetweenPlayersWhoDisagreeAndThePointMoves()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(spectator: true);
        var results = Results();
        await results.SubmitAsync(P2, Report(1), default);
        await results.SubmitAsync(P1, Report(0), default);
        Assert.Equal("[0,1]", await ScoresAsync());
        await results.SubmitAsync(Spec, Report(0), default);
        Assert.Equal("[1,0]", await ScoresAsync());
        Assert.Equal("0", (string?)await Db.StringGetAsync($"ranked_set_score:{Set}:{Match}"));
    }

    [SkippableFact]
    public async Task ACustomGameKeepsNoScoreAndAPlayerWithNoSetLeavesAPendingWinner()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(custom: true);
        await Results().SubmitAsync(P1, Report(0), default);
        Assert.Equal("[0,0]", await ScoresAsync());
        Assert.False(await Db.KeyExistsAsync($"ranked_set_pending_winner:{Match}"));

        await CleanAsync();
        await SeedAsync(set: false);
        await Results().SubmitAsync(P1, Report(1), default);
        Assert.Equal("1", (string?)await Db.StringGetAsync($"ranked_set_pending_winner:{Match}"));
    }

    [SkippableFact]
    public async Task WithNoSessionOnlyTheGameAndTheStreamHearOfIt()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var answer = await Results().SubmitAsync(null, Report(0), default);
        Assert.Empty(answer);
        Assert.True(await Db.KeyExistsAsync($"game_result_received:{Match}"));
        Assert.False(await Db.KeyExistsAsync(MatchResults.ReportsKey(Match)));
        Assert.Equal("[0,0]", await ScoresAsync());
        Assert.Single(await Db.StreamRangeAsync(MatchResults.Stream));
    }

    [SkippableFact]
    // TS read the rating before by "2v2" and after by "1v1": an FFA match's RpDelta was elo_2v2 - elo_1v1.
    public async Task OneModeRuleSoAnFfaReportHasNoDelta()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Db.StringSetAsync(Match, Js.Stringify(new JsonObject { ["players"] = new JsonArray(), ["mode"] = "ffa", ["isCustomGame"] = true }));
        await Mongo.GetCollection<BsonDocument>("eloratings").InsertOneAsync(new BsonDocument { { "account_id", P1 }, { "elo_1v1", 1000 }, { "elo_2v2", 400 }, { "wins_2v2", 3 }, { "losses_2v2", 1 } });
        var answer = await Results().SubmitAsync(P1, Report(0), default);
        Assert.Equal(0, answer["RpDelta"]!.GetValue<double>());
        Assert.Equal("2v2", answer["Mode"]!.GetValue<string>());
        Assert.Equal(4, answer["TotalGamesPlayedForMode"]!.GetValue<double>());
    }

    [SkippableFact]
    // A leave said over HTTP rides the same stream and reaches the match's settlement; a result entry is not a leave.
    public async Task ALeaveOnTheStreamReachesTheSettlementOnce()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var events = new StatusEvents();
        var stream = Stream(new Missions(), new Rifts(), new Stats(), events);
        await stream.EnsureGroupAsync(Db);
        await MatchLeaves.AppendAsync(Db, Match, P2);
        await Results().SubmitAsync(P1, Report(0), default);
        Assert.Equal(2, await stream.ReadAsync(Db, default));
        Assert.Equal([(Match, P2)], events.Left);
        Assert.Empty((await Db.StreamPendingAsync(MatchResults.Stream, MatchResultStream.Group)).Consumers ?? []);
    }

    [SkippableFact]
    // Every human's report in: the stats, once, from a report that agrees with the decided winner (a player's first).
    public async Task TheStreamRecordsEachResultAndTheStatsOnceEveryoneHasReported()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(spectator: true);
        var results = Results();
        var (missions, rifts, stats) = (new Missions(), new Rifts(), new Stats());
        var stream = Stream(missions, rifts, stats);
        await stream.EnsureGroupAsync(Db);
        await results.SubmitAsync(P1, Report(0, p1Ringouts: 7), default);
        await results.SubmitAsync(P2, Report(1, p1Ringouts: 8), default);
        Assert.Equal(2, await stream.ReadAsync(Db, default));
        Assert.Empty(stats.Recorded);
        Assert.Equal(["xp " + P1 + " 0", "missions " + P1, "xp " + P2 + " 1", "missions " + P2], missions.Recorded);
        Assert.Equal(2, rifts.Results);

        await results.SubmitAsync(Spec, Report(1, p1Ringouts: 9), default);
        Assert.Equal(1, await stream.ReadAsync(Db, default));
        // The spectator decided team 1: P2's report (the first player's that agrees) is the one recorded.
        Assert.Equal(8, Assert.Single(stats.Recorded)["PlayerMissionUpdates"]![P1]!["Stat:Game:Character:TotalRingouts"]!.GetValue<int>());
        Assert.Empty((await Db.StreamPendingAsync(MatchResults.Stream, MatchResultStream.Group)).Consumers ?? []);
        await stream.SweepAsync(Db, default);
        Assert.Single(stats.Recorded);
    }

    [SkippableFact]
    public async Task AMatchWhoseHumansDoNotAllReportGetsItsStatsAfterTheWait()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var stats = new Stats();
        var stream = Stream(new Missions(), new Rifts(), stats);
        await stream.EnsureGroupAsync(Db);
        await Results().SubmitAsync(P1, Report(0), default);
        await stream.ReadAsync(Db, default);
        await stream.SweepAsync(Db, default);
        Assert.Empty(stats.Recorded);

        await Db.SortedSetAddAsync(MatchResults.DueKey, Match, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
        await stream.SweepAsync(Db, default);
        Assert.Single(stats.Recorded);
        Assert.Null(await Db.SortedSetScoreAsync(MatchResults.DueKey, Match));
    }

    [SkippableFact]
    // The stream and its group gone (a flush, a failover with no persistence): the next read makes the group again.
    public async Task AConsumerGroupThatWentAwayIsMadeAgain()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var rifts = new Rifts();
        var stream = Stream(new Missions(), rifts, new Stats());
        await stream.EnsureGroupAsync(Db);
        await Db.KeyDeleteAsync(MatchResults.Stream);
        await Results().SubmitAsync(P1, Report(0), default);
        Assert.Equal(1, await stream.ReadAsync(Db, default));
        Assert.Equal(1, rifts.Results);
    }

    [SkippableFact]
    // A result whose handling failed stays pending, and is taken over and recorded once it has waited.
    public async Task AFailedResultStaysPendingAndIsTakenOver()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var rifts = new Rifts { Fail = true };
        var first = Stream(new Missions(), rifts, new Stats());
        await first.EnsureGroupAsync(Db);
        await Results().SubmitAsync(P1, Report(0), default);
        await first.ReadAsync(Db, default);
        Assert.Equal(1, (await Db.StreamPendingAsync(MatchResults.Stream, MatchResultStream.Group)).PendingMessageCount);

        rifts.Fail = false;
        var previous = MatchResultStream.ClaimAfter;
        MatchResultStream.ClaimAfter = TimeSpan.Zero;
        try
        {
            Assert.Equal(1, await Stream(new Missions(), rifts, new Stats()).ClaimAsync(Db, default));
        }
        finally
        {
            MatchResultStream.ClaimAfter = previous;
        }

        Assert.Equal(0, (await Db.StreamPendingAsync(MatchResults.Stream, MatchResultStream.Group)).PendingMessageCount);
        Assert.Equal(2, rifts.Results);
    }

    [SkippableFact]
    public async Task GameStatsAreWrittenAsMongooseWouldWithWholeDoublesCounted()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        var report = Report(0)["EndOfMatchStats"]!.AsObject();
        var p1 = report["PlayerMissionUpdates"]![P1]!.AsObject();
        p1["Stat:Game:Character:Stock:DamageTaken"] = 10.4;
        p1["Stat:Game:Character:TotalKnockbackAdded"] = new JsonObject { ["_hydra_double"] = 900 };
        p1["Fighter:Jake:Stretch"] = 3;
        p1["Marceline:Bite"] = 1;
        p1["Objective:Win"] = 5;
        await new GameStats(Services(), TimeProvider.System, NullLogger<GameStats>.Instance).RecordAsync(Match, report, default);

        var doc = await Mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", P1)).FirstAsync();
        Assert.Equal(0, doc["__v"].AsInt32);
        Assert.Equal(new BsonArray(), doc["recent_matches_2v2"]);
        Assert.Equal(new BsonDocument(), doc["characters_2v2"]);
        var jake = doc["characters_1v1"]["character_jake"].AsBsonDocument;
        Assert.Equal(new BsonInt32(88), jake["totalDamageDealt"]);
        Assert.Equal(new BsonInt32(88), jake["highestDamageDealt"]);
        Assert.Equal(new BsonInt32(2), jake["ringouts"]);
        Assert.Equal(new BsonDocument { { "Stretch", 3 }, { "Bite", 1 } }, Sorted(jake["fighterStats"].AsBsonDocument, ["Stretch", "Bite"]));
        // TS skipped the whole double (900.0) as not a JavaScript number.
        Assert.Equal(900, doc["aggregate"]["totalKnockbackAdded"].AsInt32);
        Assert.Equal(10, doc["aggregate"]["stockDamageTaken"].AsInt32);
        var recent = doc["recent_matches_1v1"][0].AsBsonDocument;
        Assert.Equal("win", recent["result"].AsString);
        Assert.IsType<BsonDouble>(recent["timestamp"]);
        Assert.Equal(BsonDocument.Parse($$"""{ "accountId": "{{P2}}", "character": "character_finn", "teamIndex": 1, "damage": 40, "ringouts": 1, "deaths": 2, "isWinner": false }"""),
            recent["players"][1].AsBsonDocument);

        var archive = await Mongo.GetCollection<BsonDocument>(GameStats.ArchiveCollection).Find(new BsonDocument("match_id", Match)).FirstAsync();
        Assert.IsType<BsonDateTime>(archive["timestamp"]);
        using var decompressor = new ZstdSharp.Decompressor();
        var json = JsonNode.Parse(Encoding.UTF8.GetString(decompressor.Unwrap(archive["compressed_data"].AsByteArray)))!;
        Assert.False(json["is_custom"]!.GetValue<bool>());
        Assert.Equal(0, json["winning_team"]!.GetValue<int>());
        // As received: the whole double stays wrapped.
        Assert.Equal(900, json["mission_updates"]![P1]!["Stat:Game:Character:TotalKnockbackAdded"]!["_hydra_double"]!.GetValue<int>());
    }

    private static BsonDocument Sorted(BsonDocument doc, string[] order) => new(order.Select(k => new BsonElement(k, doc[k])));
}
