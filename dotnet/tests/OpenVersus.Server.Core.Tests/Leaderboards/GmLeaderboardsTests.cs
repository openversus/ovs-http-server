using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Leaderboards;

/// <summary>
/// get_gm_leaderboards (the TS handler): the top of each mode by elo, players with a game in that mode only, with the
/// character each is connected with. Real Redis (database 15) and Mongo (a database of its own, dropped):
/// OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class GmLeaderboardsTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_gm_leaderboards_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private ILeaderboardService Leaderboards => _app!.Services.GetRequiredService<ILeaderboardService>();
    private IMongoCollection<BsonDocument> Ratings => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("eloratings");
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
        builder.AddLeaderboards();
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

    private async Task RatingAsync(string id, int elo1v1, int games1v1, int elo2v2, int games2v2) =>
        await Ratings.InsertOneAsync(new BsonDocument
        {
            { "account_id", id }, { "username", "p" }, { "elo_1v1", elo1v1 }, { "elo_2v2", elo2v2 }, { "wins_1v1", games1v1 }, { "losses_1v1", 0 },
            { "wins_2v2", games2v2 }, { "losses_2v2", 0 },
        });

    [SkippableFact]
    public async Task TheTopOfEachModeWithTheConnectedCharacter()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string first = NewPlayer(), second = NewPlayer(), only2v2 = NewPlayer();
        await RatingAsync(first, 1200, 3, 1000, 0);
        await RatingAsync(second, 1300, 2, 900, 1);
        await RatingAsync(only2v2, 1500, 0, 1100, 4);
        await Redis.HashSetAsync($"connections:{second}", [new HashEntry("character", "character_shaggy")]);

        var body = await Leaderboards.GmLeaderboardsAsync(default);

        // 1v1: by elo, only players with a 1v1 game (only2v2's 1500 does not count); the character the player is connected with, else Wonder Woman.
        var one = body["OneVsOne"]!.AsArray();
        Assert.Equal([second, first], one.Select(e => (string)e!["AccountId"]!));
        Assert.Equal([1L, 2L], one.Select(e => (long)e!["Rank"]!));
        Assert.Equal(["1300", "1200"], one.Select(e => e!["Score"]!.ToJsonString()));
        Assert.Equal(["character_shaggy", "character_wonder_woman"], one.Select(e => (string)e!["CharacterSlug"]!));
        var two = body["TwoVsTwo"]!.AsArray();
        Assert.Equal([only2v2, second], two.Select(e => (string)e!["AccountId"]!));
    }
}
