using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// Paying rewards: the reward tables (generated from the game's assets), the stores each kind lands in, and the
/// inventory listing what it paid. Real Mongo, a database of its own, dropped (OVS_TEST_MONGO).
/// </summary>
public sealed class RewardGrantTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_rewardgrant_tests";
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private static readonly List<string> s_live = ["miscon_battlepassdaily_s5"];
    private IMongoClient? _mongo;

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

    private ServiceProvider Services() => new ServiceCollection()
        .AddSingleton(_mongo!.GetDatabase(TestMongoDb)).AddLogging().AddSingleton(TimeProvider.System)
        .AddSingleton<IRewardTrackService, RewardTrackService>().AddSingleton<IRewardGrants, RewardGrants>()
        .AddSingleton<IInventoryService, InventoryService>().BuildServiceProvider();

    private static JsonObject Table(string slug) => new() { ["RewardGrantMethod"] = "RewardTableLookup", ["RewardGuid"] = slug, ["RewardHsda"] = slug, ["Constraints"] = new JsonArray() };

    private static JsonObject Direct(string item, int count) => new()
    {
        ["RewardGrantMethod"] = "DirectInventoryItem", ["RewardGuid"] = item, ["InventoryHsda"] = item, ["DirectInventoryItemCount"] = count, ["Constraints"] = new JsonArray(),
    };

    [Fact]
    public void TheTablesResolveWhatTheMissionsPay()
    {
        var unknown = new List<string>();
        Assert.Equal([new Grant("currency", "perk_currency", 80)], RewardData.Resolve(Table("reard_perk_currency_80"), unknown)); // (sic): the asset's Slug
        Assert.Equal([new Grant("currency", "match_toasts", 10)], RewardData.Resolve(Table("reward_toast_10"), unknown));
        Assert.Equal([new Grant("xp", "XP:Event:MRT:Battlepass", 100)], RewardData.Resolve(Table("reward_xp_battlepass_tiny"), unknown));
        Assert.Equal([new Grant("currency", "match_toasts", 5)], RewardData.Resolve(Direct("match_toasts", 5), unknown));
        Assert.Equal([new Grant("item", "banner_like_bodacious", 1)], RewardData.Resolve(Direct("banner_like_bodacious", 1), unknown));
        Assert.Empty(unknown);
        Assert.Empty(RewardData.Resolve(Table("reward_nope"), unknown));
        Assert.Single(unknown);
        Assert.Equal("mrt_mastery_c003", RewardData.CharacterTrack("character_superman"));
    }

    [SkippableFact]
    public async Task EachKindLandsInItsStoreAndTheInventoryListsWhatItLacks()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await using var services = Services();
        string id = ObjectId.GenerateNewId().ToString();
        var paid = await services.GetRequiredService<IRewardGrants>().GrantAsync(id,
            [Table("reard_perk_currency_80"), Table("reward_toast_10"), Table("reward_xp_battlepass_tiny"), Table("reward_xp_fighter_road_300"),
             Direct("banner_like_bodacious", 1), Direct("item_added_later", 2), Table("reard_perk_currency_80")], s_live, default);

        // Every reward is told, each once, as given.
        Assert.Equal(7, paid.RewardsGranted.Count);
        // Recorded, owned or not; perk currency twice is 160.
        var items = await services.GetRequiredService<IRewardGrants>().ItemsAsync(id, default);
        Assert.Equal(160, items["perk_currency"]);
        Assert.Equal(1, items["banner_like_bodacious"]);
        Assert.Equal(2, items["item_added_later"]);
        Assert.False(items.ContainsKey("match_toasts"));
        // Toasts on the counters the TS server reads (100 to start).
        var counters = await _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playercounters").Find(new BsonDocument("accountId", id)).FirstAsync();
        Assert.Equal(110, counters["match_toasts"].ToInt64());
        // Battle pass XP to the live Season 5 pass only; Fighter Road XP (dead) nowhere.
        Assert.Equal(["mrt_battlepass_season_five"], paid.ChangedTracks.Select(t => t["TrackSlug"]!.GetValue<string>()));
        Assert.Equal(100, paid.ChangedTracks[0]["CurrentScore"]!.GetValue<int>());

        // The inventory: perk currency and the unknown item added; the banner (unlock-all may hold it) not twice.
        var inventory = (await services.GetRequiredService<IInventoryService>().InventoryAsync(id, default))!.OfType<JsonObject>().ToList();
        var perk = inventory.Single(i => i["item_slug"]?.GetValue<string>() == "perk_currency");
        Assert.Equal(160, perk["count"]!.GetValue<long>());
        Assert.Equal("simple", perk["result_type"]!.GetValue<string>());
        Assert.Equal(2, inventory.Single(i => i["item_slug"]?.GetValue<string>() == "item_added_later")["count"]!.GetValue<long>());
        Assert.True(inventory.Count(i => i["item_slug"]?.GetValue<string>() == "banner_like_bodacious") <= 1);
    }

    [SkippableFact]
    public async Task APlayerPaidNothingGetsTheSameInventory()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        await using var services = Services();
        string id = ObjectId.GenerateNewId().ToString();
        var withService = (await services.GetRequiredService<IInventoryService>().InventoryAsync(id, default))!;
        Assert.DoesNotContain(withService.OfType<JsonObject>(), i => i["item_slug"]?.GetValue<string>() == "perk_currency");
    }
}
