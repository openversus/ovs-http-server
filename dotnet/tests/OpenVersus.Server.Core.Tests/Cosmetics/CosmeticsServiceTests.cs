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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
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
}
