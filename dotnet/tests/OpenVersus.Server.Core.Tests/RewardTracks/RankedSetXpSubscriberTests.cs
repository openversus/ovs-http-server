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
/// End Game's ranked-set XP as the match flow pays it (RankedSetXpSubscriber): once per set and player, onto the battle
/// pass, the account level and the played character's level, whose completed tiers are paid at once. Real Mongo (a
/// database of its own, dropped: OVS_TEST_MONGO) and Redis (OVS_TEST_REDIS).
/// </summary>
public sealed class RankedSetXpSubscriberTests : IAsyncLifetime
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

    private static RankedSetXpSubscriber Subscriber(ServiceProvider services) => new(services,
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RewardTrackSettings>>(),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MissionSettings>>(),
        services.GetRequiredService<IRewardTrackService>(), services.GetRequiredService<IRewardGrants>(), NullLogger<RankedSetXpSubscriber>.Instance);

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
