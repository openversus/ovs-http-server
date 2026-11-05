using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>Walking out of matches (docs/BANS.md): what counts, the first-leaver rule, the window and the lockout. Redis database 4 (OVS_TEST_REDIS).</summary>
public sealed class QuitTrackerTests : IAsyncLifetime
{
    private const string Me = "0000000000000000000a0001", Other = "0000000000000000000a0002", Third = "0000000000000000000a0003";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_quit_tests";
    private MongoClient? _mongo;
    private static readonly DateTimeOffset s_now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private ConnectionMultiplexer? _redis;
    private readonly QuitSettings _settings = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis);
    private IDatabase Db => _redis!.GetDatabase();

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        _redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } },
            User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER"),
            Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW"),
            DefaultDatabase = 4,
            AllowAdmin = true,
        });
        await _redis.GetServers()[0].FlushDatabaseAsync(4);
        if (!string.IsNullOrEmpty(s_mongo))
        {
            _mongo = new MongoClient(new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl());
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    private QuitTracker Tracker()
    {
        var collection = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!);
        if (_mongo is not null)
        {
            collection.AddSingleton(_mongo.GetDatabase(TestMongoDb));
        }

        return new QuitTracker(collection.BuildServiceProvider(), new TestOptions<QuitSettings>(_settings), NullLogger<QuitTracker>.Instance);
    }

    private IMongoCollection<BsonDocument> Players => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(global::OpenVersus.Server.Core.Access.PlayerRecord.Collection);

    private async Task<(int Total, int Timeouts)> CountersAsync(string playerId)
    {
        var doc = await Players.Find(new BsonDocument("_id", ObjectId.Parse(playerId))).FirstOrDefaultAsync();
        return (doc?.GetValue(QuitTracker.TotalField, 0).ToInt32() ?? 0, doc?.GetValue(QuitTracker.TimeoutsField, 0).ToInt32() ?? 0);
    }

    private async Task MatchFoundAsync(params string[] players)
    {
        await QuitTracker.GroupAsync(Db, players);
        foreach (string p in players)
        {
            await Db.HashSetAsync($"player:{p}", "status", "in_match");
        }
    }

    [SkippableFact]
    public async Task OnlyAMatchmadeMatchCountsAndOnlyItsFirstLeaver()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        // In the menus: nothing.
        var idle = await Tracker().ReportAsync(Me, s_now);
        Assert.Equal((false, "not in a matchmade match", 0), (idle.Counted, idle.Reason, idle.Count));

        await MatchFoundAsync(Me, Other);
        var first = await Tracker().ReportAsync(Me, s_now);
        Assert.Equal((true, 1, null), (first.Counted, first.Count, first.LockedUntil));
        // The other player, stuck, leaves the same match: not counted; and Me again later in the same match is still the first.
        var stuck = await Tracker().ReportAsync(Other, s_now.AddSeconds(30));
        Assert.Equal((false, "not the first to leave", 0), (stuck.Counted, stuck.Reason, stuck.Count));
        Assert.Equal(0, await Tracker().RecentAsync(Other, s_now.AddSeconds(30)));

        // After the match ends (status idle) a quit is a menu quit, unless a Casual rematch vote is open.
        await Db.HashSetAsync($"player:{Me}", "status", "idle");
        Assert.False((await Tracker().ReportAsync(Me, s_now.AddMinutes(1))).Counted);
        await MatchFoundAsync(Me, Other);
        await Db.HashSetAsync($"player:{Me}", "status", "idle");
        await Db.StringSetAsync(RematchVotes.CasualPlayerKey(Me), "match1");
        var rematch = await Tracker().ReportAsync(Me, s_now.AddMinutes(2));
        Assert.Equal((true, 2), (rematch.Counted, rematch.Count));
    }

    [SkippableFact]
    public async Task ARankedSetIsItsOwnGroupAndABotMatchNeverCounts()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Db.StringSetAsync($"player_ranked_set:{Me}", "set9");
        await Db.StringSetAsync($"player_ranked_set:{Other}", "set9");
        Assert.True((await Tracker().ReportAsync(Me, s_now)).Counted);
        Assert.Equal("not the first to leave", (await Tracker().ReportAsync(Other, s_now)).Reason);

        await MatchFoundAsync(Third, "Bot00000000000000000000000000001");
        var bots = await Tracker().ReportAsync(Third, s_now);
        Assert.Equal((false, "a match against bots"), (bots.Counted, bots.Reason));
    }

    [SkippableFact]
    public async Task ThreeInTheWindowLockMatchmakingAndTheRefusalCarriesTheBanner()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        if (_mongo is not null)
        {
            await Players.InsertManyAsync([new BsonDocument { { "_id", ObjectId.Parse(Me) }, { "name", "Me" } }, new BsonDocument { { "_id", ObjectId.Parse(Other) }, { "name", "Other" } }]);
        }

        for (int i = 0; i < 2; i++)
        {
            await MatchFoundAsync(Me, Other);
            Assert.Null((await Tracker().ReportAsync(Me, s_now.AddMinutes(i * 5))).LockedUntil);
        }

        // One outside the window does not count toward it: the oldest is 31 min before the third.
        await MatchFoundAsync(Me, Other);
        Assert.Null((await Tracker().ReportAsync(Me, s_now.AddMinutes(31))).LockedUntil);
        await MatchFoundAsync(Me, Other);
        var third = await Tracker().ReportAsync(Me, s_now.AddMinutes(32));
        Assert.Equal((true, 3, s_now.AddMinutes(32 + 15)), (third.Counted, third.Count, third.LockedUntil));
        Assert.Equal(third.LockedUntil, await Tracker().LockedUntilAsync(Me, s_now.AddMinutes(40)));
        Assert.Null(await Tracker().LockedUntilAsync(Me, s_now.AddMinutes(48)));
        Assert.Null(await Tracker().LockedUntilAsync(Other, s_now.AddMinutes(40)));
        if (_mongo is not null)
        {
            // Every counted quit for good and the one lockout on the account; the player stuck behind a leaver gets nothing.
            await MatchFoundAsync(Me, Other);
            await Tracker().ReportAsync(Me, s_now.AddMinutes(33));
            await Tracker().ReportAsync(Other, s_now.AddMinutes(33).AddSeconds(5));
            Assert.Equal((5, 1), await CountersAsync(Me));
            Assert.Equal((0, 0), await CountersAsync(Other));
        }

        var log = new TestLogger();
        Assert.True(await Tracker().RefuseLockedAsync([Other, Me], log, "1v1 matchmaking"));
        Assert.False(await Tracker().RefuseLockedAsync([Other], log, "1v1 matchmaking"));
        var queued = await Db.ListRangeAsync(PlayerMessages.NotificationPrefix + Me);
        var banner = Assert.Single(queued);
        Assert.Contains("Go touch some grass", banner.ToString());
        Assert.Contains("minutes to touch some grass", banner.ToString());
        Assert.DoesNotContain("{minutes}", banner.ToString());
        Assert.Contains(log.Lines, l => l.Contains("Refused 1v1 matchmaking") && l.Contains(Me));
    }

    [SkippableFact]
    public async Task OffCountsNothingButStillLogsAndStillNamesTheFirstLeaver()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _settings.Enabled = false;
        await MatchFoundAsync(Me, Other);
        var off = await Tracker().ReportAsync(Me, s_now);
        Assert.Equal((false, "counting is off", 0), (off.Counted, off.Reason, off.Count));
        Assert.Equal("not the first to leave", (await Tracker().ReportAsync(Other, s_now)).Reason);
    }

    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
