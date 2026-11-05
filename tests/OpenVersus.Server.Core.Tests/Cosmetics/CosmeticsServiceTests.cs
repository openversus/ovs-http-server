using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Cosmetics;

/// <summary>
/// Where the cosmetics writes deliberately differ from the TS server, and the JS semantics they keep
/// (tools/cosmetics/equip_diff.mjs checks everything else against the TS server). Real Redis (database 15) and Mongo (a
/// database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class CosmeticsServiceTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_cosmetics_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private ICosmeticsService Cosmetics => _app!.Services.GetRequiredService<ICosmeticsService>();
    private IMongoCollection<BsonDocument> Stored => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("cosmetics");
    private IDatabase Redis => _app!.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase(TestRedisDb);

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);


    private string NewPlayer()
    {
        string id = ObjectId.GenerateNewId().ToString();
        _ids.Add(id);
        return id;
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var mongoUrl = new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb };
        _seedMongo = new MongoClient(mongoUrl.ToMongoUrl());
        await _seedMongo.DropDatabaseAsync(TestMongoDb);
        await _seedMongo.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("dataassets").InsertManyAsync(
        [
            new BsonDocument { { "assetType", "CharacterData" }, { "slug", "character_shaggy" }, { "character_slug", "" }, { "enabled", true } },
            new BsonDocument { { "assetType", "ProfileIconData" }, { "slug", "profile_icon_bat" }, { "character_slug", "" }, { "enabled", true } },
        ]);

        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("cosmeticstest", "TEST_PORT", 1, 1),
        [
            "--TEST_PORT=0", "--Control:Port=0", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddCosmetics();
        _app = builder.Build();
        _app.UseOpenVersus();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            foreach (string id in _ids)
            {
                await Redis.KeyDeleteAsync($"player:{id}:cosmetics");
                await Redis.KeyDeleteAsync($"connections:{id}:cosmetics");
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task<BsonDocument?> DocumentAsync(string id) => await Stored.Find(new BsonDocument("_id", ObjectId.Parse(id))).FirstOrDefaultAsync();

    [SkippableFact]
    public async Task ATauntWrittenIntoTheDefaultsDoesNotChangeTheDefaults()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // A cache without Taunts: the TS server hands out its module-level defaults there and writes the slot into them.
        string first = NewPlayer();
        await Redis.StringSetAsync($"player:{first}:cosmetics", $$"""{"_id":"{{first}}","account_id":"{{first}}","Banner":"banner_x","__v":0}""");
        Assert.True(await Cosmetics.EquipTauntAsync(first, "character_shaggy", 1, "taunt_mine", default));
        var written = (JsonObject)JsonNode.Parse((await Redis.StringGetAsync($"player:{first}:cosmetics")).ToString())!;
        Assert.Equal("taunt_mine", written["Taunts"]!["character_shaggy"]!["TauntSlots"]![1]!.GetValue<string>());

        // The next new player still gets the schema's defaults.
        string next = NewPlayer();
        await Cosmetics.EquippedAsync(next, default);
        var slots = (await DocumentAsync(next))!["Taunts"]["character_shaggy"]["TauntSlots"].AsBsonArray;
        Assert.Equal("emote_generic_heart", slots[1].AsString);
    }

    [SkippableTheory]
    [InlineData(5, 6)]
    [InlineData(CosmeticsService.MaxSlotIndex, CosmeticsService.MaxSlotIndex + 1)]
    public async Task ASlotPastTheEndPadsWithNull(int index, int length)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = NewPlayer();
        Assert.True(await Cosmetics.EquipStatTrackerAsync(id, index, "st", default));
        var slots = (await DocumentAsync(id))!["StatTrackers"]["StatTrackerSlots"].AsBsonArray;
        Assert.Equal(length, slots.Count);
        Assert.Equal("st", slots[index].AsString);
        Assert.True(slots[3].IsBsonNull);
    }

    [SkippableFact]
    public async Task ASlotPastTheLimitWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = NewPlayer();
        Assert.False(await Cosmetics.EquipStatTrackerAsync(id, CosmeticsService.MaxSlotIndex + 1, "st", default));
        Assert.False(await Cosmetics.EquipTauntAsync(id, "character_shaggy", 1_000_000_000, "x", default));
        Assert.Null(await DocumentAsync(id));
        Assert.False(await Redis.KeyExistsAsync($"player:{id}:cosmetics"));
    }

    [SkippableTheory]
    // JS sets these as properties of the array, which its JSON never shows: the slots stay as they were, and are written.
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"01\"")]
    [InlineData("null")]
    public async Task AnIndexThatIsNotASlotChangesNoSlot(string index)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = NewPlayer();
        Assert.True(await Cosmetics.EquipStatTrackerAsync(id, JsonNode.Parse(index), "st", default));
        Assert.Equal(new BsonArray { "", "", "" }, (await DocumentAsync(id))!["StatTrackers"]["StatTrackerSlots"].AsBsonArray);
    }

    [SkippableFact]
    public async Task AnIndexAsCanonicalTextIsASlot()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = NewPlayer();
        Assert.True(await Cosmetics.EquipStatTrackerAsync(id, "2", "st", default));
        Assert.Equal(new BsonArray { "", "", "st" }, (await DocumentAsync(id))!["StatTrackers"]["StatTrackerSlots"].AsBsonArray);
    }

    [SkippableFact]
    public async Task AnUnknownIconWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = NewPlayer();
        Assert.False(await Cosmetics.SetProfileIconAsync(id, "profile_icon_nope", default));
        Assert.Null(await DocumentAsync(id));
        Assert.True(await Cosmetics.SetProfileIconAsync(id, "profile_icon_bat", default));
        Assert.NotNull(await DocumentAsync(id));
    }

    [SkippableFact]
    public async Task AnEquipRefreshesTheMatchCopy()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // The TS lobby lock reads connections:{id}:cosmetics before anything else; TS equips leave it stale.
        string id = NewPlayer();
        await Redis.HashSetAsync($"connections:{id}:cosmetics", [new HashEntry("Banner", "\"banner_old\""), new HashEntry("Stale", "1")]);
        Assert.True(await Cosmetics.EquipAsync(CosmeticSlot.Banner, id, "banner_new", true, default));

        var copy = (await Redis.HashGetAllAsync($"connections:{id}:cosmetics")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var saved = (JsonObject)JsonNode.Parse((await Redis.StringGetAsync($"player:{id}:cosmetics")).ToString())!;
        Assert.Equal("\"banner_new\"", copy["Banner"]);
        // Every field of the saved value, each as JSON.stringify writes it (the TS readers JSON.parse each one).
        foreach (var (name, value) in saved)
        {
            Assert.Equal(value!.ToJsonString(), JsonNode.Parse(copy[name])!.ToJsonString());
        }

        Assert.Equal($"\"{id}\"", copy["_id"]);
        Assert.Equal("1", copy["Stale"]);
    }

    [SkippableFact]
    public async Task AnEquipCreatesNoMatchCopy()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // The TS websocket deletes the copy at disconnect; one made here would outlive the session.
        string id = NewPlayer();
        Assert.True(await Cosmetics.EquipAsync(CosmeticSlot.Banner, id, "banner_new", true, default));
        Assert.True(await Cosmetics.EquipTauntAsync(id, "character_shaggy", 1, "taunt_mine", default));
        Assert.True(await Redis.KeyExistsAsync($"player:{id}:cosmetics"));
        Assert.False(await Redis.KeyExistsAsync($"connections:{id}:cosmetics"));
    }
}
