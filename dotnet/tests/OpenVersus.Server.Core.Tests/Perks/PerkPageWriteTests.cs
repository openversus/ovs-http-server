using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Perks;

/// <summary>
/// perks_set_character_page where it deliberately differs from the TS server, and its failure answer
/// (tools/perks/perks_write_diff.mjs compares the rest with the TS server). Real Redis (database 15) and Mongo (a
/// database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
public sealed class PerkPageWriteTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_perk_page_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IPerksService Perks => _app!.Services.GetRequiredService<IPerksService>();
    private IMongoCollection<BsonDocument> Pages => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("perkpages");
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
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("perkstest", "TEST_PORT", 1, 1),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddPerks();
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
                await Redis.KeyDeleteAsync($"connections:{id}");
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private static JsonObject Page(JsonNode? character, JsonNode? index) => new()
    {
        ["Character"] = character, ["PageIndex"] = index, ["DisplayName"] = "Page", ["Description"] = "d", ["Perks"] = new JsonArray("a", "b"),
    };

    [SkippableFact]
    public async Task APageIsStoredUnderItsCharacterAndIndex()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        var (status, answer) = await Perks.SetPageAsync(player, Page("character_shaggy", 1), default);
        Assert.Equal(200, status);
        Assert.Equal("""{"body":{},"metadata":null,"return_code":0}""", answer.ToJsonString());
        var stored = await Pages.Find(new BsonDocument("account_id", ObjectId.Parse(player))).SingleAsync();
        Assert.Equal(0, stored["__v"].AsInt32);
        Assert.Equal("Page", stored["perk_pages"]["character_shaggy"]["1"]["DisplayName"].AsString);
    }

    [SkippableTheory]
    // A Mongo path of their own (the dot nests, "$" is an operator), or not text or a number: nothing is written, and it
    // answers as saved.
    [InlineData("\"a.b\"", "0")]
    [InlineData("\"$set\"", "0")]
    [InlineData("\"character_shaggy\"", "\"1.2\"")]
    [InlineData("{}", "0")]
    [InlineData("\"character_shaggy\"", "[1]")]
    public async Task APageWithoutAUsablePathIsNotWritten(string character, string index)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        var (status, _) = await Perks.SetPageAsync(player, Page(JsonNode.Parse(character), JsonNode.Parse(index)), default);
        Assert.Equal(200, status);
        Assert.Equal(0, await Pages.CountDocumentsAsync(new BsonDocument("account_id", ObjectId.Parse(player))));
    }

    private async Task<BsonDocument> StoredPagesAsync(string player) =>
        (await Pages.Find(new BsonDocument("account_id", ObjectId.Parse(player))).SingleAsync())["perk_pages"].AsBsonDocument;

    [SkippableTheory]
    // TS stores these as null.
    [InlineData("{}")]
    [InlineData("""{"DisplayName":null,"Description":null,"Perks":null}""")]
    public async Task ANewPagesMissingFieldsGetTheGamesDefaults(string fields)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        var body = JsonNode.Parse(fields)!.AsObject();
        body["Character"] = "character_shaggy";
        body["PageIndex"] = 1;
        await Perks.SetPageAsync(player, body, default);
        Assert.Equal(
            new BsonDocument { { "DisplayName", "Custom Set 2" }, { "Description", "" }, { "Perks", new BsonArray() } },
            (await StoredPagesAsync(player))["character_shaggy"]["1"].AsBsonDocument);
    }

    [SkippableFact]
    public async Task AStoredPageKeepsWhatTheRequestLeavesOut()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        await Perks.SetPageAsync(player, new JsonObject
        {
            ["Character"] = "character_shaggy", ["PageIndex"] = 0, ["DisplayName"] = "Mine", ["Description"] = "notes", ["Perks"] = new JsonArray("a", "b", "c", "d"),
        }, default);
        // New perks only; and an empty description is the player's choice, not a missing one.
        await Perks.SetPageAsync(player, new JsonObject
        {
            ["Character"] = "character_shaggy", ["PageIndex"] = 0, ["Description"] = "", ["Perks"] = new JsonArray("e", "f", "g", "h"),
        }, default);
        Assert.Equal(
            new BsonDocument { { "DisplayName", "Mine" }, { "Description", "" }, { "Perks", new BsonArray { "e", "f", "g", "h" } } },
            (await StoredPagesAsync(player))["character_shaggy"]["0"].AsBsonDocument);
    }

    [SkippableTheory]
    [InlineData(null, "\"\"")]
    [InlineData("\"character_shaggy\"", "null")]
    public async Task AMissingPageIndexIsTheFirstPage(string? character, string index)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        await Redis.HashSetAsync($"connections:{player}", "character", "character_shaggy");
        await Perks.SetPageAsync(player, Page(character is null ? null : JsonNode.Parse(character), JsonNode.Parse(index)), default);
        Assert.True((await StoredPagesAsync(player))["character_shaggy"].AsBsonDocument.Contains("0"));
    }

    [SkippableTheory]
    // The connection's character, else the player record's, else the default.
    [InlineData("character_taz", "character_finn", "character_taz")]
    [InlineData(null, "character_finn", "character_finn")]
    [InlineData(null, null, "character_wonder_woman")]
    public async Task AMissingCharacterIsThePlayersCurrentOne(string? connection, string? record, string expected)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        if (connection is not null)
        {
            await Redis.HashSetAsync($"connections:{player}", "character", connection);
        }

        var players = _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");
        await players.InsertOneAsync(record is null ? new BsonDocument("_id", ObjectId.Parse(player)) : new BsonDocument { { "_id", ObjectId.Parse(player) }, { "character", record } });
        await Perks.SetPageAsync(player, Page(null, 0), default);
        Assert.Equal([expected], (await StoredPagesAsync(player)).Names);
    }

    [SkippableFact]
    public async Task ASessionThatIsNotAPlayerIsRefusedAsThere()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var (status, answer) = await Perks.SetPageAsync("not-a-player", Page("character_shaggy", 0), default);
        Assert.Equal(500, status);
        Assert.Equal("""{"body":{"message":"Error saving perks"},"metadata":null,"return_code":1}""", answer.ToJsonString());
        Assert.Equal(0, await Pages.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
