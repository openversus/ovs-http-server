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
/// get_gm_leaderboards: the Grandmaster tier, the 100 players with the best Master-rated (2500) fighter in each mode,
/// each once with that fighter, by its rating. Real Redis (database 15) and Mongo (a database of its own, dropped): OVS_TEST_REDIS,
/// OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
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

    private async Task RatingAsync(string id, int elo1v1, BsonDocument characters1v1, BsonDocument? characters2v2 = null) =>
        await Ratings.InsertOneAsync(new BsonDocument
        {
            { "account_id", id }, { "username", "p" }, { "elo_1v1", elo1v1 }, { "elo_2v2", 1000 }, { "wins_1v1", 5 }, { "losses_1v1", 0 },
            { "wins_2v2", 1 }, { "losses_2v2", 0 }, { "characters_1v1", characters1v1 }, { "characters_2v2", characters2v2 ?? new BsonDocument() },
        });

    private static BsonDocument Fighter(int elo) => new() { { "elo", elo }, { "wins", 3 }, { "losses", 1 } };

    [SkippableFact]
    public async Task EachPlayersBestMasterFighterOnceBestFirstPerMode()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string twoMasters = NewPlayer(), oneMaster = NewPlayer(), diamond = NewPlayer();
        // The overall rating does not decide it (twoMasters' is lowest): the fighters' ratings do; a Diamond fighter (2499) is
        // out, and twoMasters' second Master fighter (bugs, 2510) is not listed: one entry per player, their best.
        await RatingAsync(twoMasters, 1800, new BsonDocument { { "character_shaggy", Fighter(2650) }, { "character_bugs", Fighter(2510) } });
        await RatingAsync(oneMaster, 2900, new BsonDocument { { "character_taz", Fighter(2600) }, { "character_bugs", Fighter(2499) } },
            characters2v2: new BsonDocument("character_taz", Fighter(2700)));
        await RatingAsync(diamond, 2450, new BsonDocument("character_shaggy", Fighter(2450)));

        var body = await Leaderboards.GmLeaderboardsAsync(default);

        var one = body["OneVsOne"]!.AsArray();
        Assert.Equal([(twoMasters, "character_shaggy"), (oneMaster, "character_taz")],
            one.Select(e => ((string)e!["AccountId"]!, (string)e!["CharacterSlug"]!)));
        Assert.Equal([1, 2], one.Select(e => (int)e!["Rank"]!));
        Assert.Equal(["2650", "2600"], one.Select(e => e!["Score"]!.ToJsonString()));
        var two = body["TwoVsTwo"]!.AsArray();
        Assert.Equal([(oneMaster, "character_taz")], two.Select(e => ((string)e!["AccountId"]!, (string)e!["CharacterSlug"]!)));
    }

    // The tier holds 100: the Grandmaster floor is the 100th entry's rating, not a threshold, and it moves with the list.
    [SkippableFact]
    public async Task TheFloorIsThe100thEntrysRating()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // 101 Master fighters rated 2505 to 2605, one per player; the 2505 one is the 101st.
        var lowest = "";
        for (int i = 0; i <= 100; i++)
        {
            string id = NewPlayer();
            await RatingAsync(id, 1000, new BsonDocument("character_shaggy", Fighter(2505 + i)));
            if (i == 0)
            {
                lowest = id;
            }
        }

        var one = (await Leaderboards.GmLeaderboardsAsync(default))["OneVsOne"]!.AsArray();
        Assert.Equal(LeaderboardService.GrandmasterSize, one.Count);
        Assert.Equal("2506", one[^1]!["Score"]!.ToJsonString());
        Assert.DoesNotContain(lowest, one.Select(e => (string)e!["AccountId"]!));

        // The 2505 fighter wins up to 2507: in, and the 2506 one is out.
        await Ratings.UpdateOneAsync(new BsonDocument("account_id", lowest), new BsonDocument("$set", new BsonDocument("characters_1v1.character_shaggy.elo", 2507)));
        one = (await Leaderboards.GmLeaderboardsAsync(default))["OneVsOne"]!.AsArray();
        Assert.Equal("2507", one[^1]!["Score"]!.ToJsonString());
        Assert.Contains(lowest, one.Select(e => (string)e!["AccountId"]!));
    }
}
