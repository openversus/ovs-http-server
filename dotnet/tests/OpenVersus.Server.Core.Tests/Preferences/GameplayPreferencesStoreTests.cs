using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Preferences;

/// <summary>
/// Storing a player's GameplayPreferences: only a value, never the default over one, 0 included (tools/ssc/preferences_diff.mjs
/// compares the whole route with the TS server's). Real Redis (database 15) and Mongo (a database of its own, dropped):
/// OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class GameplayPreferencesStoreTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_gameplay_prefs_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IGameplayPreferencesStore Store => _app!.Services.GetRequiredService<IGameplayPreferencesStore>();
    private IMongoCollection<BsonDocument> Players => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");
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
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("prefstest", "TEST_PORT", 1, 1),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddGameplayPreferences();
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
                await Redis.KeyDeleteAsync($"connections:198.51.100.{_ids.IndexOf(id)}");
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task<string> PlayerAsync(bool session = true)
    {
        string id = NewPlayer();
        await Players.InsertOneAsync(new BsonDocument { { "_id", ObjectId.Parse(id) }, { "GameplayPreferences", 448 } });
        if (session)
        {
            await Redis.HashSetAsync($"connections:{id}", [new("id", id), new("GameplayPreferences", "448")]);
        }

        return id;
    }

    private async Task<BsonValue> StoredAsync(string id) => (await Players.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync())["GameplayPreferences"];

    [SkippableTheory]
    [InlineData("0", 0)]
    [InlineData("964", 964)]
    [InlineData("\"12\"", 12)]
    public async Task AValueIsStoredOnTheRecordAndTheSession(string raw, int expected)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = await PlayerAsync();
        Assert.Equal(expected, await Store.SaveAsync(id, JsonNode.Parse(raw), null, default));
        Assert.Equal(new BsonInt32(expected), await StoredAsync(id));
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)await Redis.HashGetAsync($"connections:{id}", "GameplayPreferences"));
    }

    [SkippableTheory]
    // Never the default (964) over the player's value.
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("1.5")]
    public async Task NoValueLeavesThePlayersAsItIs(string? raw)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = await PlayerAsync();
        Assert.Null(await Store.SaveAsync(id, raw is null ? null : JsonNode.Parse(raw), null, default));
        Assert.Equal(new BsonInt32(448), await StoredAsync(id));
        Assert.Equal("448", (string?)await Redis.HashGetAsync($"connections:{id}", "GameplayPreferences"));
    }

    [SkippableFact]
    public async Task ASessionIsNotCreated()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = await PlayerAsync(session: false);
        await Store.SaveAsync(id, 972, null, default);
        Assert.Equal(new BsonInt32(972), await StoredAsync(id));
        Assert.False(await Redis.KeyExistsAsync($"connections:{id}"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheIpCopyOnlyWhileItIsThePlayers(bool owned)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = await PlayerAsync();
        string ip = $"198.51.100.{_ids.IndexOf(id)}";
        await Redis.HashSetAsync($"connections:{ip}", [new("id", owned ? id : "someone-else"), new("GameplayPreferences", "448")]);
        await Store.SaveAsync(id, 972, ip, default);
        Assert.Equal(owned ? "972" : "448", (string?)await Redis.HashGetAsync($"connections:{ip}", "GameplayPreferences"));
    }

    [SkippableFact]
    public async Task ASessionThatIsNotAPlayerWritesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Null(await Store.SaveAsync("not-a-player", 972, null, default));
    }
}
