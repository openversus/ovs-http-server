using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Profiles;

/// <summary>
/// GET /accounts/wb_network/{id} (the TS handler): the player by id, else by public_id, in the TS shape; {} otherwise.
/// Real Redis (database 15) and Mongo (a database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER,
/// OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class WbNetworkAccountTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_wb_network_account_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IProfilesService Profiles => _app!.Services.GetRequiredService<IProfilesService>();
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
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("rankedtest", "TEST_PORT", 1, 1),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddProfiles();
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

    [SkippableFact]
    public async Task ByIdThenByPublicIdElseNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string named = NewPlayer(), nameless = NewPlayer(), byPublic = NewPlayer();
        await Players.InsertManyAsync(
        [
            new BsonDocument { { "_id", ObjectId.Parse(named) }, { "name", "Alpha" } },
            new BsonDocument { { "_id", ObjectId.Parse(nameless) } },
            new BsonDocument { { "_id", ObjectId.Parse(byPublic) }, { "name", "Beta" }, { "public_id", "pub-beta" } },
        ]);

        Assert.Equal($$"""{"id":"{{named}}","identity.default_username":true,"identity.username":"Alpha","presence":"online","presence_state":1}""",
            (await Profiles.WbNetworkAccountAsync(named, default)).ToJsonString());
        // A missing name leaves the key out, as JSON drops player.name when it is undefined.
        Assert.Equal($$"""{"id":"{{nameless}}","identity.default_username":true,"presence":"online","presence_state":1}""",
            (await Profiles.WbNetworkAccountAsync(nameless, default)).ToJsonString());
        Assert.Equal("Beta", (string)(await Profiles.WbNetworkAccountAsync("pub-beta", default))["identity.username"]!);
        Assert.Equal("{}", (await Profiles.WbNetworkAccountAsync(ObjectId.GenerateNewId().ToString(), default)).ToJsonString());
        Assert.Equal("{}", (await Profiles.WbNetworkAccountAsync("not-an-id", default)).ToJsonString());
    }
}
