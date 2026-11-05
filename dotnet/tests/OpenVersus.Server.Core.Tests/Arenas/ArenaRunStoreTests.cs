using OpenVersus.Server.Core.Arenas;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>Where Arena runs live (ArenaRunStore), on real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]).</summary>
[Collection(RedisTestDatabase.Name)]
public sealed class ArenaRunStoreTests : IAsyncLifetime
{
    private const string Run = "0000000000000000000c0001", Me = "0000000000000000000c0002";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly ArenaRules Rules = ArenaRules.Default;
    private static readonly IArenaBotMatches Bots = new RandomArenaBotMatches();

    private static readonly IReadOnlyList<(string Character, string[] Skins)> Roster =
        [.. Enumerable.Range(1, 12).Select(n => ($"character_{n}", new[] { $"skin_{n}_default" }))];

    private ConnectionMultiplexer? _redis;

    private IDatabase Db => _redis!.GetDatabase();

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
        await Db.KeyDeleteAsync(ArenaRunStore.Key(Run));
        await Db.SortedSetRemoveAsync(ArenaRunStore.DeadlinesKey, Run);
    }

    // Me and a bot on team 0, bots everywhere else.
    private static ArenaRun NewRun(Random random) => ArenaRun.Start(Run,
        [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
            [.. Enumerable.Range(0, 2).Select(i => t == 0 && i == 0 ? (Me, "Me", false) : ($"bot{t}{i}", "Bot", true))])],
        Roster, Rules, random, 1_000).Run;

    [Fact]
    // Everything a run holds survives its record: a run in its shop, with a pick, coins, pages and pairings.
    public void ARunReadsBackAsItWasWritten()
    {
        var run = NewRun(new Random(1));
        run.Select(Me, run.Players[Me].Pool[0], null, true, Roster, Rules, new Random(2), 2_000);
        string json = ArenaRunStore.ToJson(run);

        var read = ArenaRunStore.FromJson(json)!;

        Assert.Equal(json, ArenaRunStore.ToJson(read));
        Assert.Equal((ArenaPhase.Shop, true), (read.Phase, read.Players[Me].RandomPick));
        Assert.Contains("\"Phase\":\"Shop\"", json);
        Assert.Null(ArenaRunStore.FromJson("not a run"));
    }

    [SkippableFact]
    // A new run is written once, with its deadline; a second create of its id writes nothing.
    public async Task ARunIsCreatedOnceWithItsDeadline()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        var run = NewRun(new Random(3));

        Assert.True(await ArenaRunStore.CreateAsync(Db, run));
        Assert.False(await ArenaRunStore.CreateAsync(Db, NewRun(new Random(4))));

        Assert.Equal(ArenaRunStore.ToJson(run), ArenaRunStore.ToJson((await ArenaRunStore.GetAsync(Db, Run))!));
        Assert.Equal(run.DeadlineMs, await Db.SortedSetScoreAsync(ArenaRunStore.DeadlinesKey, Run));
        Assert.True(await Db.KeyTimeToLiveAsync(ArenaRunStore.Key(Run)) > TimeSpan.FromMinutes(59));
    }

    [SkippableFact]
    // An accepted transition is written with its new deadline (the shop's, then the match's), and one that leaves the run
    // with none removes its entry; a refused one writes nothing; no run, nothing.
    public async Task ATransitionIsWrittenOnlyWhenAccepted()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await ArenaRunStore.CreateAsync(Db, NewRun(new Random(5)));
        string before = (await Db.StringGetAsync(ArenaRunStore.Key(Run))).ToString();

        var (_, refused) = await ArenaRunStore.UpdateAsync(Db, Run, r => r.ShopAbsent(Me, 1, Rules, Bots, new Random(6), 2_000));
        Assert.False(refused.Accepted);
        Assert.Equal(before, (await Db.StringGetAsync(ArenaRunStore.Key(Run))).ToString());

        var (shop, _) = await ArenaRunStore.UpdateAsync(Db, Run, r => r.SelectAbsent(Me, Roster, Rules, new Random(7), 2_000));
        Assert.Equal(ArenaPhase.Shop, shop!.Phase);
        Assert.Equal(shop.DeadlineMs, await Db.SortedSetScoreAsync(ArenaRunStore.DeadlinesKey, Run));
        Assert.Equal([Run], await ArenaRunStore.DueAsync(Db, shop.DeadlineMs));
        Assert.Empty(await ArenaRunStore.DueAsync(Db, shop.DeadlineMs - 1));

        var (match, step) = await ArenaRunStore.UpdateAsync(Db, Run, r => r.ShopAbsent(Me, 1, Rules, Bots, new Random(8), 3_000));
        Assert.True(step.Accepted);
        Assert.Equal(ArenaPhase.Match, match!.Phase);
        Assert.Equal(match.DeadlineMs, await Db.SortedSetScoreAsync(ArenaRunStore.DeadlinesKey, Run));
        Assert.Equal(ArenaPhase.Match, (await ArenaRunStore.GetAsync(Db, Run))!.Phase);
        await ArenaRunStore.UpdateAsync(Db, Run, r =>
        {
            r.DeadlineMs = 0;
            return new ArenaStep(true, []);
        });
        Assert.Null(await Db.SortedSetScoreAsync(ArenaRunStore.DeadlinesKey, Run));

        var (none, nothing) = await ArenaRunStore.UpdateAsync(Db, "0000000000000000000c0003", r => new ArenaStep(true, []));
        Assert.Null(none);
        Assert.False(nothing.Accepted);
    }

    [SkippableFact]
    // A write between the read and the transition's write (another replica's): the transition runs again on what that
    // writer left, and both changes land.
    public async Task ATransitionThatLosesARaceRunsAgainOnTheNewRun()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await ArenaRunStore.CreateAsync(Db, NewRun(new Random(9)));
        int runs = 0;

        var (run, step) = await ArenaRunStore.UpdateAsync(Db, Run, r =>
        {
            if (runs++ == 0)
            {
                var other = ArenaRunStore.FromJson(ArenaRunStore.ToJson(r))!;
                other.Players[Me].Coins = 99;
                Db.StringSet(ArenaRunStore.Key(Run), ArenaRunStore.ToJson(other));
            }

            return r.SelectAbsent(Me, Roster, Rules, new Random(10), 2_000);
        });

        Assert.Equal(2, runs);
        Assert.True(step.Accepted);
        var stored = (await ArenaRunStore.GetAsync(Db, Run))!;
        Assert.Equal((99, ArenaPhase.Shop), (stored.Players[Me].Coins, stored.Phase));
        Assert.Equal(ArenaRunStore.ToJson(run!), ArenaRunStore.ToJson(stored));
    }
}
