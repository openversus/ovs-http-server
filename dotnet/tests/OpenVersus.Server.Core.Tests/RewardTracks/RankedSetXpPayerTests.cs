using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.RewardTracks;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// End Game's ranked-set XP as the match flow pays it (RankedSetXpPayer): once per set and player, onto the battle
/// pass, the account level and the played character's level, whose completed tiers are paid at once. Real Mongo (a
/// database of its own, dropped: OVS_TEST_MONGO) and Redis (OVS_TEST_REDIS).
/// </summary>
public sealed class RankedSetXpPayerTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_rankedsetxp_tests";
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private IMongoClient? _mongo;
    private ConnectionMultiplexer? _redis;

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(s_mongo) && !string.IsNullOrEmpty(s_redis))
        {
            _mongo = new MongoClient(s_mongo);
            await _mongo.DropDatabaseAsync(TestMongoDb);
            _redis = await ConnectionMultiplexer.ConnectAsync(s_redis);
        }
    }

    public async Task DisposeAsync()
    {
        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }

        _redis?.Dispose();
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddSingleton(_mongo!.GetDatabase(TestMongoDb)).AddSingleton<IConnectionMultiplexer>(_redis!).AddLogging().AddSingleton(TimeProvider.System)
        .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RewardTrackSettings>>(new TestOptions<RewardTrackSettings>(new RewardTrackSettings { PerPlayer = true, CharacterMastery = true }))
        .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<MissionSettings>>(new TestOptions<MissionSettings>(new MissionSettings()))
        .AddSingleton<IRewardTrackService, RewardTrackService>().AddSingleton<IRewardGrants, RewardGrants>()
        .AddSingleton<IInventoryService, InventoryService>().BuildServiceProvider();

    private static RankedSetXpPayer Subscriber(ServiceProvider services, IRewardTrackService? tracks = null) => new(services,
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RewardTrackSettings>>(),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MissionSettings>>(),
        tracks ?? services.GetRequiredService<IRewardTrackService>(), services.GetRequiredService<IRewardGrants>(), NullLogger<RankedSetXpPayer>.Instance);

    // The real tracks, with a failure where a test asks for one: the next AddScoreAsync calls, or every ClaimAllAsync.
    private sealed class FailingTracks(IRewardTrackService real) : IRewardTrackService
    {
        public int AddScoreFailures { get; set; }
        public bool ClaimAllFails { get; set; }

        public Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct) => real.AnswerAsync(accountId, ct);

        public Task<IReadOnlyList<JsonObject>> AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct) =>
            AddScoreFailures-- > 0 ? throw new MongoException("a write failed") : real.AddScoreAsync(accountId, points, ct);

        public Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAllAsync(string accountId, string trackSlug, CancellationToken ct) =>
            ClaimAllFails ? throw new MongoException("a claim failed") : real.ClaimAllAsync(accountId, trackSlug, ct);

        public Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAsync(string accountId, string trackSlug, IReadOnlyCollection<string>? tierGuids, CancellationToken ct) =>
            real.ClaimAsync(accountId, trackSlug, tierGuids, ct);
    }

    private static string Set(string player, bool won, string character, string setKey) =>
        new JsonObject { ["playerId"] = player, ["won"] = won, ["character"] = character, ["setKey"] = setKey, ["source"] = "test" }.ToJsonString();

    private static async Task<long> Score(ServiceProvider services, string player, string track) =>
        ((await services.GetRequiredService<IRewardTrackService>().AnswerAsync(player, default))["body"]!["RewardTrackStates"]!.AsArray()
            .OfType<JsonObject>().Single(t => t["TrackSlug"]!.GetValue<string>() == track)["CurrentScore"]!.GetValue<long>());

    [SkippableFact]
    public async Task ASetPaysOncePerPlayer()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        using var services = Services();
        var subscriber = Subscriber(services);
        string winner = ObjectId.GenerateNewId().ToString(), loser = ObjectId.GenerateNewId().ToString();
        string setKey = $"ranked:{Guid.NewGuid()}";

        await subscriber.HandleAsync(Set(winner, true, "character_shaggy", setKey));
        await subscriber.HandleAsync(Set(winner, true, "character_shaggy", setKey)); // the same set again
        await subscriber.HandleAsync(Set(loser, false, "character_BananaGuard", setKey));

        Assert.Equal(450, await Score(services, winner, "mrt_battlepass_season_five"));
        Assert.Equal(600, await Score(services, winner, "mrt_mastery_account"));
        Assert.Equal(600, await Score(services, winner, "mrt_mastery_shaggy"));
        Assert.Equal(300, await Score(services, loser, "mrt_battlepass_season_five"));
        Assert.Equal(400, await Score(services, loser, "mrt_mastery_banana_guard"));
    }

    [SkippableFact]
    // Nothing paid (the score's write failed): the claim is given back and the record's failure goes to the stream, which
    // keeps it pending; its retry pays, once.
    public async Task AFailureBeforeTheScoreIsWrittenLeavesTheRecordForItsRetry()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        using var services = Services();
        var tracks = new FailingTracks(services.GetRequiredService<IRewardTrackService>()) { AddScoreFailures = 1 };
        var payer = Subscriber(services, tracks);
        string player = ObjectId.GenerateNewId().ToString(), setKey = $"ranked:{Guid.NewGuid()}";

        await Assert.ThrowsAsync<MongoException>(() => payer.PayAsync(Set(player, true, "character_shaggy", setKey)));
        Assert.False(await _redis!.GetDatabase().KeyExistsAsync($"ranked_set_xp:{setKey}:{player}"));

        await payer.PayAsync(Set(player, true, "character_shaggy", setKey));
        await payer.PayAsync(Set(player, true, "character_shaggy", setKey));
        Assert.Equal(450, await Score(services, player, "mrt_battlepass_season_five"));
    }

    [SkippableFact]
    // The score written, then the tiers failed: the record is done (no throw, the claim kept), so no retry pays it twice.
    public async Task AFailureAfterTheScoreIsWrittenEndsTheRecord()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        using var services = Services();
        var tracks = new FailingTracks(services.GetRequiredService<IRewardTrackService>()) { ClaimAllFails = true };
        var payer = Subscriber(services, tracks);
        string player = ObjectId.GenerateNewId().ToString(), setKey = $"ranked:{Guid.NewGuid()}";

        await payer.PayAsync(Set(player, true, "character_shaggy", setKey));
        Assert.True(await _redis!.GetDatabase().KeyExistsAsync($"ranked_set_xp:{setKey}:{player}"));
        await payer.PayAsync(Set(player, true, "character_shaggy", setKey));
        Assert.Equal(450, await Score(services, player, "mrt_battlepass_season_five"));
    }

    [SkippableFact]
    public async Task PaymentsArrivingTogetherAllLand()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        using var services = Services();
        var subscriber = Subscriber(services);
        string player = ObjectId.GenerateNewId().ToString();
        // Eight wins at once for one player (2026-10-05: four of eight were lost to the tracks' version guard).
        await Task.WhenAll(Enumerable.Range(1, 8).Select(i => subscriber.PayAsync(Set(player, true, "character_shaggy", $"ranked:{Guid.NewGuid()}"))));

        // Shaggy's level passes his Fighter Pass tier 5 (3,000) on the way: 600 more battle pass XP.
        Assert.Equal(8 * 450 + 600, await Score(services, player, "mrt_battlepass_season_five"));
        Assert.Equal(8 * 600, await Score(services, player, "mrt_mastery_account"));
    }

    [SkippableFact]
    public async Task AFighterPassTierIsPaidAtOnceAndItsBattlePassXpLands()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        using var services = Services();
        var subscriber = Subscriber(services);
        string player = ObjectId.GenerateNewId().ToString();
        // 2,500 on Shaggy's level, then a won set (+600) crosses its Fighter Pass tier 5 (3,000: 600 battle pass XP).
        await services.GetRequiredService<IRewardTrackService>().AddScoreAsync(player, new Dictionary<string, int> { ["mrt_mastery_shaggy"] = 2_500 }, default);
        await subscriber.HandleAsync(Set(player, true, "character_shaggy", $"ranked:{Guid.NewGuid()}"));

        Assert.Equal(3_100, await Score(services, player, "mrt_mastery_shaggy"));
        Assert.Equal(450 + 600, await Score(services, player, "mrt_battlepass_season_five"));
        // Every completed tier's rewards are claimed: nothing is left to claim by hand.
        var (_, left) = await services.GetRequiredService<IRewardTrackService>().ClaimAllAsync(player, "mrt_mastery_shaggy", default);
        Assert.Empty(left);
    }
}
