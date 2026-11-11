using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Inventory;
using StackExchange.Redis;
using Xunit;

namespace OpenVersus.Server.Core.Tests.Inventory;

/// <summary>
/// An asset's retirement (<see cref="AssetRetirement"/>) over documents as their writers leave them: playeritems as
/// RewardGrants pays (items.{slug}), playertesters' variant and profile_icon, a cosmetics document and its Redis copies
/// as CosmeticsService keeps them, a perk page, dataassets rows as POST /syncAsset writes them, and a player online.
/// Mongo in a database of its own, dropped (OVS_TEST_MONGO); Redis database 15 (OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class AssetRetirementTests : IAsyncLifetime
{
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string TestMongoDb = "ovs_asset_retirement_tests";
    private const string Skin = "skin_test_retired", Default = "skin_test_default", Icon = "profileicon_test_instead", OldIcon = "profileicon_test_retired",
        Emote = "emote_test_retired", Banner = "banner_test_retired", OtherBanner = "banner_test_default", Taunt = "taunt_test_retired", OtherTaunt = "taunt_test_other",
        Perk = "perk_test_retired", OtherPerk = "perk_test_other", Fighter = "character_test";
    private static readonly string P1 = "0000000000000000002e0001", P2 = "0000000000000000002e0002", P3 = "0000000000000000002e0003";

    private IMongoClient? _mongo;
    private ConnectionMultiplexer? _redis;

    private static bool Configured => !string.IsNullOrEmpty(s_mongo) && !string.IsNullOrEmpty(s_redis);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        string[] parts = s_redis!.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();

        await Of("dataassets").InsertManyAsync(
        [
            Row(Skin, "SkinData", "character_Jason"), Row(Default, "SkinData", "character_jason"), Row("skin_test_elsewhere", "SkinData", "character_finn"),
            Row(Icon, "ProfileIconData", ""), Row(OldIcon, "ProfileIconData", ""), Row(Emote, "EmoteData", ""),
            Row(Banner, "BannerData", ""), Row(OtherBanner, "BannerData", ""),
            Row(Taunt, "TauntData", "character_jason"), Row(OtherTaunt, "TauntData", "character_jason"),
            Row(Perk, "MvsPerkHsda", ""), Row(OtherPerk, "MvsPerkHsda", ""), Row(Fighter, "CharacterData", ""),
        ]);
        await Of("config").InsertOneAsync(new BsonDocument("CRC", 10));
        await Of("playeritems").InsertManyAsync(
        [
            new BsonDocument { { "_id", ObjectId.Parse(P1) }, { "items", new BsonDocument { { Skin, 1 }, { "emote_other", 2 } } } },
            // Holds the replacement already, more of it: it keeps the higher count.
            new BsonDocument { { "_id", ObjectId.Parse(P2) }, { "items", new BsonDocument { { Skin, 1 }, { Icon, 3 } } } },
            new BsonDocument { { "_id", ObjectId.Parse(P3) }, { "items", new BsonDocument { { "emote_other", 1 }, { Emote, 1 }, { OldIcon, 1 } } } },
        ]);
        await Of("playertesters").InsertManyAsync(
        [
            new BsonDocument { { "_id", ObjectId.Parse(P1) }, { "name", Skin }, { "character", "character_jason" }, { "variant", Skin }, { "profile_icon", "profile_icon_default" } },
            new BsonDocument { { "_id", ObjectId.Parse(P3) }, { "character", "character_jason" }, { "variant", "skin_test_other" }, { "profile_icon", OldIcon } },
        ]);
        // As CosmeticsService stores them, and its two Redis copies (the cache it reads first, the match's copy).
        var cosmetics = new BsonDocument
        {
            { "_id", ObjectId.Parse(P2) }, { "account_id", ObjectId.Parse(P2) },
            { "Taunts", new BsonDocument("character_jason", new BsonDocument("TauntSlots", new BsonArray { Taunt, "", Taunt, OtherTaunt })) },
            { "AnnouncerPack", "announcer_pack_default" }, { "Banner", Banner },
            { "StatTrackers", new BsonDocument("StatTrackerSlots", new BsonArray { "stat_tracking_bundle_default", "stat_tracking_bundle_default", "stat_tracking_bundle_default" }) },
            { "RingoutVfx", "ring_out_vfx_default" }, { "__v", 0 },
        };
        await Of("cosmetics").InsertOneAsync(cosmetics);
        await Db.StringSetAsync($"player:{P2}:cosmetics", $$"""{"Banner":"{{Banner}}","RingoutVfx":"ring_out_vfx_default"}""");
        await Db.HashSetAsync($"connections:{P2}:cosmetics", [new("Banner", $"\"{Banner}\""), new("RingoutVfx", "\"ring_out_vfx_default\"")]);
        await Of("perkpages").InsertOneAsync(new BsonDocument
        {
            { "account_id", ObjectId.Parse(P1) },
            { "perk_pages", new BsonDocument("character_jason", new BsonDocument("0", new BsonDocument { { "DisplayName", "Custom Set 1" }, { "Perks", new BsonArray { Perk, "perk_b", "perk_c", Perk } } })) },
        });
        // A collection nothing here rewrites: a match's history.
        await Of("matchhistory").InsertOneAsync(new BsonDocument("players", new BsonArray { new BsonDocument("skin", Skin) }));

        await Db.SetAddAsync("online_players", [P1, P3]);
        await Db.HashSetAsync($"player:{P1}", [new("character", "character_jason"), new("skin", Skin)]);
        await Db.HashSetAsync($"connections:{P1}", [new("character", "character_jason"), new("skin", Skin), new("username", "one")]);
        await Db.HashSetAsync($"player:{P3}", [new("character", "character_jason"), new("skin", "skin_test_other"), new("profileIcon", OldIcon)]);
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
        await Db.SetRemoveAsync("online_players", [P1, P3]);
        await Db.KeyDeleteAsync([$"player:{P1}", $"connections:{P1}", $"player:{P3}", $"player:{P2}:cosmetics", $"connections:{P2}:cosmetics"]);
    }

    private IDatabase Db => _redis!.GetDatabase();
    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);
    private IMongoCollection<BsonDocument> Of(string collection) => Mongo.GetCollection<BsonDocument>(collection);

    private static BsonDocument Row(string slug, string type, string character, bool enabled = true) =>
        new() { { "slug", slug }, { "assetType", type }, { "character_slug", character }, { "enabled", enabled }, { "assetPath", $"/Test/{slug}.{slug}" } };

    private Task<(AssetRetirementView? View, string? Refused)> RunAsync(bool apply, string asset = Skin, string? replaceWith = Icon, string? equip = Default) =>
        AssetRetirement.RunAsync(Mongo, Db, asset, replaceWith, equip, apply, NullLogger.Instance, default, search: true);

    private async Task<BsonDocument> ItemsAsync(string id) => (await Of("playeritems").Find(new BsonDocument("_id", ObjectId.Parse(id))).FirstAsync())["items"].AsBsonDocument;

    private async Task<BsonDocument> PlayerAsync(string id) => await Of("playertesters").Find(new BsonDocument("_id", ObjectId.Parse(id))).FirstAsync();

    private async Task<int> CrcAsync() => (await Of("config").Find(FilterDefinition<BsonDocument>.Empty).FirstAsync())["CRC"].ToInt32();

    [SkippableFact]
    public async Task ADryRunCountsAndWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        var (view, refused) = await RunAsync(apply: false);

        Assert.Null(refused);
        Assert.False(view!.Applied);
        Assert.Equal("SkinData", view.AssetType);
        Assert.Equal((2, 1, 1), (view.Owners, view.SessionsChanged, view.AssetRowsRemoved));
        Assert.Equal([new AssetMention("playertesters", 1)], view.Equipped);
        Assert.True(view.ReplacementInCatalog);
        Assert.Equal([new AssetMention("dataassets", 1), new AssetMention("matchhistory", 1), new AssetMention("playeritems", 2), new AssetMention("playertesters", 1)], view.StillNamedIn);
        Assert.Empty(view.NotSearched);
        // Unasked, the database is not searched (the counts are the same).
        var (quick, _) = await AssetRetirement.RunAsync(Mongo, Db, Skin, Icon, Default, apply: false, NullLogger.Instance, default);
        Assert.False(quick!.Searched);
        Assert.Empty(quick.StillNamedIn);
        Assert.Equal((2, 1, 1), (quick.Owners, quick.SessionsChanged, quick.AssetRowsRemoved));

        Assert.Equal(1, (await ItemsAsync(P1))[Skin].AsInt32);
        Assert.Equal(Skin, (await PlayerAsync(P1))["variant"].AsString);
        Assert.Equal(1, await Of("dataassets").CountDocumentsAsync(new BsonDocument("slug", Skin)));
        Assert.Equal(10, await CrcAsync());
        Assert.Equal(Skin, (string?)await Db.HashGetAsync($"player:{P1}", "skin"));
        Assert.Equal(Skin, (string?)await Db.HashGetAsync($"connections:{P1}", "skin"));
    }

    [SkippableFact]
    public async Task ARetiredSkinsOwnersHoldTheReplacementAndItsWearersTheFallback()
    {
        Skip.IfNot(Configured, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        var (view, refused) = await RunAsync(apply: true);

        Assert.Null(refused);
        Assert.True(view!.Applied);
        Assert.Equal((2, 1, 1), (view.Owners, view.SessionsChanged, view.AssetRowsRemoved));
        Assert.Equal([new AssetMention("playertesters", 1)], view.Equipped);
        // The owners hold the replacement (the higher count where they had it), the skin's entry is gone, the rest stays.
        Assert.Equal(new BsonDocument { { "emote_other", 2 }, { Icon, 1 } }, await ItemsAsync(P1));
        Assert.Equal(new BsonDocument(Icon, 3), await ItemsAsync(P2));
        Assert.False((await ItemsAsync(P3)).Contains(Icon));
        // Who wore it wears the fallback: the slot, not a name that happens to read the same; another skin is left alone.
        var one = await PlayerAsync(P1);
        Assert.Equal((Default, Skin), (one["variant"].AsString, one["name"].AsString));
        Assert.Equal("skin_test_other", (await PlayerAsync(P3))["variant"].AsString);
        // Its catalog row is gone and the catalog's number moved, once.
        Assert.Equal(0, await Of("dataassets").CountDocumentsAsync(new BsonDocument("slug", Skin)));
        Assert.Equal(11, await CrcAsync());
        // The player online: the lobby pick and the session, the rest of the session kept.
        Assert.Equal(Default, (string?)await Db.HashGetAsync($"player:{P1}", "skin"));
        Assert.Equal(Default, (string?)await Db.HashGetAsync($"connections:{P1}", "skin"));
        Assert.Equal("one", (string?)await Db.HashGetAsync($"connections:{P1}", "username"));
        Assert.Equal("skin_test_other", (string?)await Db.HashGetAsync($"player:{P3}", "skin"));
        // What nothing here rewrites is said: the history, and the player named like it.
        Assert.Equal([new AssetMention("matchhistory", 1), new AssetMention("playertesters", 1)], view.StillNamedIn);

        // Again: nothing left to change, and the catalog's number stays. A skin equipped again since is found again.
        var (again, _) = await RunAsync(apply: true);
        Assert.Equal((0, 0, 0), (again!.Owners, again.SessionsChanged, again.AssetRowsRemoved));
        Assert.Empty(again.Equipped);
        Assert.Null(again.AssetType);
        Assert.Equal(11, await CrcAsync());
        await Of("playertesters").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(P3)), new BsonDocument("$set", new BsonDocument("variant", Skin)));
        var (third, _) = await RunAsync(apply: true);
        Assert.Equal([new AssetMention("playertesters", 1)], third!.Equipped);
        Assert.Equal(Default, (await PlayerAsync(P3))["variant"].AsString);
    }

    [SkippableFact]
    // Any kind with a slot: a profile icon (the player's record, the lobby pick), a banner and a taunt (the cosmetics and
    // their Redis copies), a perk (a perk page). With no replacement the owner's entry is just removed.
    public async Task EveryKindOfSlotHoldsItsFallback()
    {
        Skip.IfNot(Configured, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        var (icon, _) = await RunAsync(apply: true, asset: OldIcon, replaceWith: null, equip: Icon);
        Assert.Equal(("ProfileIconData", 1, 1), (icon!.AssetType, icon.Owners, icon.SessionsChanged));
        Assert.Equal([new AssetMention("playertesters", 1)], icon.Equipped);
        Assert.Equal(Icon, (await PlayerAsync(P3))["profile_icon"].AsString);
        Assert.Equal(Icon, (string?)await Db.HashGetAsync($"player:{P3}", "profileIcon"));
        Assert.False((await ItemsAsync(P3)).Contains(OldIcon));
        Assert.False((await ItemsAsync(P3)).Contains(Icon));

        var (banner, _) = await RunAsync(apply: true, asset: Banner, replaceWith: null, equip: OtherBanner);
        Assert.Equal([new AssetMention("cosmetics", 1)], banner!.Equipped);
        Assert.Equal(1, banner.SessionsChanged);
        var cosmetics = await Of("cosmetics").Find(new BsonDocument("_id", ObjectId.Parse(P2))).FirstAsync();
        Assert.Equal(OtherBanner, cosmetics["Banner"].AsString);
        Assert.Equal(new BsonArray { Taunt, "", Taunt, OtherTaunt }, cosmetics["Taunts"]["character_jason"]["TauntSlots"]);
        // The cache is read before Mongo: it goes, to be read from Mongo again; the match's copy is rewritten.
        Assert.False(await Db.KeyExistsAsync($"player:{P2}:cosmetics"));
        Assert.Equal($"\"{OtherBanner}\"", (string?)await Db.HashGetAsync($"connections:{P2}:cosmetics", "Banner"));
        Assert.Equal("\"ring_out_vfx_default\"", (string?)await Db.HashGetAsync($"connections:{P2}:cosmetics", "RingoutVfx"));

        await RunAsync(apply: true, asset: Taunt, replaceWith: null, equip: OtherTaunt);
        cosmetics = await Of("cosmetics").Find(new BsonDocument("_id", ObjectId.Parse(P2))).FirstAsync();
        Assert.Equal(new BsonArray { OtherTaunt, "", OtherTaunt, OtherTaunt }, cosmetics["Taunts"]["character_jason"]["TauntSlots"]);

        var (perk, _) = await RunAsync(apply: true, asset: Perk, replaceWith: null, equip: OtherPerk);
        Assert.Equal([new AssetMention("perkpages", 1)], perk!.Equipped);
        var page = (await Of("perkpages").Find(new BsonDocument("account_id", ObjectId.Parse(P1))).FirstAsync())["perk_pages"]["character_jason"]["0"];
        Assert.Equal(new BsonArray { OtherPerk, "perk_b", "perk_c", OtherPerk }, page["Perks"]);
        Assert.Equal("Custom Set 1", page["DisplayName"].AsString);

        // Nobody has it in a slot: no fallback is needed.
        var (emote, refused) = await RunAsync(apply: true, asset: Emote, replaceWith: null, equip: null);
        Assert.Null(refused);
        Assert.Equal((1, 1), (emote!.Owners, emote.AssetRowsRemoved));
        Assert.Equal(new BsonDocument("emote_other", 1), await ItemsAsync(P3));
    }

    [SkippableFact]
    public async Task WhatCannotBeDoneIsRefusedAndWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_MONGO and OVS_TEST_REDIS to run");
        // A fighter is more than a slot's value.
        Assert.Contains("is a fighter", (await RunAsync(apply: true, asset: Fighter, replaceWith: null, equip: null)).Refused);
        // The fallback: an enabled asset of the same kind, and of the same fighter.
        Assert.Contains("is a ProfileIconData", (await RunAsync(apply: true, equip: Icon)).Refused);
        Assert.Contains("not an enabled asset", (await RunAsync(apply: true, equip: "skin_test_missing")).Refused);
        Assert.Contains("character_finn's", (await RunAsync(apply: true, equip: "skin_test_elsewhere")).Refused);
        Assert.Contains("not an asset's slug", (await RunAsync(apply: true, replaceWith: "items.x")).Refused);

        // A replacement the catalog does not hold: a dry run says so, applying is refused.
        var (dry, _) = await RunAsync(apply: false, replaceWith: "profileicon_test_unsynced");
        Assert.False(dry!.ReplacementInCatalog);
        Assert.Contains("sync its row first", (await RunAsync(apply: true, replaceWith: "profileicon_test_unsynced")).Refused);

        // Someone has it equipped and no fallback is named: a dry run counts them, applying is refused whole.
        var (unnamed, _) = await RunAsync(apply: false, equip: null);
        Assert.Equal([new AssetMention("playertesters", 1)], unnamed!.Equipped);
        Assert.Contains("--equip", (await RunAsync(apply: true, equip: null)).Refused);

        Assert.Equal(1, (await ItemsAsync(P1))[Skin].AsInt32);
        Assert.Equal(Skin, (await PlayerAsync(P1))["variant"].AsString);
        Assert.Equal(1, await Of("dataassets").CountDocumentsAsync(new BsonDocument("slug", Skin)));
        Assert.Equal(10, await CrcAsync());
    }
}
