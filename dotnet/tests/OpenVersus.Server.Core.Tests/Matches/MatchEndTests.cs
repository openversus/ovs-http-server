using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// What only the C# match end does (MatchEnd, DelayedMessages): a match ends once, and a delayed message is sent once.
/// Parity with the TS websocket's match end is tools/matches/match_end_diff.mjs. Real Redis, database 15 (OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchEndTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"00000000000000000020{n:D4}";
    private static readonly string Match = Id(100), P1 = Id(1);

    private ConnectionMultiplexer? _redis;
    private readonly List<string> _sent = [];

    private sealed class Sets : IRankedSets
    {
        public int Ended { get; private set; }

        public Task CheckinAsync(string playerId, string? containerMatchId) => Task.CompletedTask;
        public Task ConcedeAsync(string playerId) => Task.CompletedTask;
        public Task FaceoffTimeoutAsync(string playerId) => Task.CompletedTask;
        public Task RecordWinnerAsync(IReadOnlyList<string> playerIds, string matchId, int winner) => Task.CompletedTask;

        public Task<GameEndResult> GameEndedAsync(string matchId, IReadOnlyList<string> playerIds, JsonObject? config, bool counts)
        {
            Ended++;
            return Task.FromResult(GameEndResult.Of(GameEnd.NotASet));
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis))
        {
            return;
        }

        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains(P1, StringComparison.Ordinal))
            {
                lock (_sent)
                {
                    _sent.Add(m.ToString());
                }
            }
        });
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }
    }

    private async Task CleanAsync()
    {
        foreach (var key in _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(15, "*00000000000000000020*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync(DelayedMessages.Key);
    }

    private IDatabase Db => _redis!.GetDatabase();

    private async Task<int> SentAsync(int atLeast, int waitMs = 1500)
    {
        for (int waited = 0; waited < waitMs; waited += 50)
        {
            lock (_sent)
            {
                if (_sent.Count >= atLeast)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        await Task.Delay(200);
        lock (_sent)
        {
            return _sent.Count;
        }
    }

    [SkippableFact]
    public async Task AMatchEndsOnce()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings());
        var sets = new Sets();
        IMatchEnd end = new MatchEnd(services, sets, new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance),
            new TestOptions<SeasonSettings>(new SeasonSettings()), TimeProvider.System, NullLogger<MatchEnd>.Instance);
        await Db.StringSetAsync(GameplayConfigs.Key(P1), Js.Stringify(new JsonObject
        {
            ["data"] = new JsonObject { ["GameplayConfig"] = new JsonObject { ["MatchId"] = Match } },
            ["payload"] = new JsonObject { ["match"] = new JsonObject { ["id"] = Match } },
        }));

        // A relay and a host's node both report the end, or the rollback server retries: the second changes nothing.
        await end.EndAsync(Match, [P1]);
        await end.EndAsync(Match, [P1]);

        Assert.Equal(1, sets.Ended);
        Assert.Equal(1, await SentAsync(1));
        Assert.Contains("EndOfMatchPayload", _sent[0], StringComparison.Ordinal);
        Assert.False(await Db.KeyExistsAsync(GameplayConfigs.Key(P1)));
    }

    [SkippableFact]
    public async Task ADelayedMessageIsSentOnceWhenDueAndNotBefore()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider();
        var sweep = new DelayedMessageSweep(services, TimeProvider.System, NullLogger<DelayedMessageSweep>.Instance);
        await DelayedMessages.ScheduleAsync(Db, TimeProvider.System, [P1], new JsonObject { ["data"] = new JsonObject { ["template_id"] = "Later" } }, TimeSpan.FromMilliseconds(400));

        await sweep.SweepAsync(Db);
        Assert.Equal(0, await SentAsync(1, waitMs: 100));

        await Task.Delay(450);
        // Two replicas sweeping the same entry: one of them sends it.
        await Task.WhenAll(sweep.SweepAsync(Db), sweep.SweepAsync(Db));
        Assert.Equal(1, await SentAsync(1));
        Assert.Equal(0, await Db.SortedSetLengthAsync(DelayedMessages.Key));
    }
}
