using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Ops;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests;

/// <summary>
/// The ops service against a real Redis and Mongo, seeded with the TS server's shapes. Isolated from any live data:
/// Redis database 15 (the TS server uses 0) and a Mongo database of its own, dropped afterwards. Runs with
/// OVS_TEST_REDIS (host:port, plus OVS_TEST_REDIS_USER / OVS_TEST_REDIS_PW) and OVS_TEST_MONGO (a mongodb:// URI; its
/// database name is replaced).
/// </summary>
public sealed class OpsTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_ctl_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private WebApplication? _app;
    private ConnectionMultiplexer? _seedRedis;
    private IMongoClient? _seedMongo;
    private IOpsService Ops => _app!.Services.GetRequiredService<IOpsService>();
    private IDatabase Redis => _seedRedis!.GetDatabase(TestRedisDb);
    private IMongoCollection<BsonDocument> Players => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");

    private readonly ObjectId _alice = ObjectId.GenerateNewId(), _bob = ObjectId.GenerateNewId(), _dup1 = ObjectId.GenerateNewId(), _dup2 = ObjectId.GenerateNewId();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        string user = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "", pw = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "";
        _seedRedis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } },
            User = user,
            Password = pw,
        });
        var mongoUrl = new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb };
        _seedMongo = new MongoClient(mongoUrl.ToMongoUrl());
        await _seedMongo.DropDatabaseAsync(TestMongoDb);
        await ClearRedisAsync();
        await SeedAsync();

        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("opstest", "TEST_PORT", 1, 1),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}", $"--REDIS_USERNAME={user}", $"--REDIS_PW={pw}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        _app = builder.Build();
        _app.UseOpenVersus();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedRedis is not null)
        {
            await ClearRedisAsync();
            _seedRedis.Dispose();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task ClearRedisAsync()
    {
        foreach (var server in _seedRedis!.GetServers())
        {
            await foreach (var key in server.KeysAsync(TestRedisDb, "*"))
            {
                await Redis.KeyDeleteAsync(key);
            }
        }
    }

    private async Task SeedAsync()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string a = _alice.ToString(), b = _bob.ToString();
        await Players.InsertManyAsync(
        [
            new BsonDocument { { "_id", _alice }, { "name", "Alice" }, { "hydraUsername", "Gen_Alice" }, { "steamId", "76561190000000001" }, { "public_id", "pub-a" } },
            new BsonDocument { { "_id", _bob }, { "name", "bob" }, { "hydraUsername", "Gen_Bob" }, { "steamId", "" } },
            new BsonDocument { { "_id", _dup1 }, { "name", "Dup" } },
            new BsonDocument { { "_id", _dup2 }, { "name", "dup" } },
        ]);

        await Redis.SetAddAsync("online_players", [a, b, "ghost"]);
        await Redis.HashSetAsync($"connections:{a}", [new HashEntry("username", "Alice"), new HashEntry("character", "character_shaggy")]);
        await Redis.HashSetAsync($"connections:{b}", [new HashEntry("hydraUsername", "Gen_Bob")]);
        await Redis.HashSetAsync($"player:{a}", "status", "queued");
        await Redis.ListRightPushAsync("1v1", $$"""{"party_size":1,"players":[{"id":"{{a}}","skill":1200,"region":"us"}],"created_at":{{now - 30}},"partyId":"party-a","matchmakingRequestId":"req-a"}""");

        // A ranked set with a game being played, a set between games, and a custom game (never listed).
        await Redis.StringSetAsync("match_started:m1", "1");
        await Redis.StringSetAsync("m1", $$"""{"players":[{"playerId":"{{a}}","teamIndex":0},{"playerId":"{{b}}","teamIndex":1}],"mode":"1v1"}""");
        await Redis.StringSetAsync($"player_ranked_set:{a}", "s1");
        await Redis.StringSetAsync("ranked_set:s1", $$"""{"players":[{"playerId":"{{a}}","teamIndex":0},{"playerId":"{{b}}","teamIndex":1}],"mode":"ranked-1v1","scores":[1,0],"gamesPlayed":1}""");
        await Redis.StringSetAsync("match_characters:s1", $$"""{"{{b}}":"character_batman"}""");
        await Redis.StringSetAsync("ranked_set:s2", """{"players":[{"playerId":"x1","teamIndex":0},{"playerId":"x2","teamIndex":1}],"mode":"ranked-2v2","scores":[0,2],"gamesPlayed":2,"conceded":true}""");
        await Redis.StringSetAsync("match_started:m3", "1");
        await Redis.StringSetAsync("m3", """{"players":[{"playerId":"x3","teamIndex":0}],"mode":"custom","isCustomGame":true}""");
    }

    [SkippableFact]
    public async Task QueuesShowWhoIsWaitingAndForHowLong()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var queues = (await Ops.QueuesAsync()).Value!;
        var oneVOne = queues.Single(q => q.Queue == "1v1");
        Assert.Equal(1, oneVOne.Tickets);
        var player = oneVOne.Entries.Single().Players.Single();
        Assert.Equal("Alice", player.Name);
        Assert.Equal(1200, player.Skill);
        Assert.InRange(oneVOne.Entries.Single().WaitingSeconds, 29, 40);
        Assert.Equal(0, queues.Single(q => q.Queue == "2v2").Tickets);
    }

    [SkippableFact]
    public async Task OnlineCountsAndNamesWithTheWebsitesFallbacks()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Null((await Ops.OnlineAsync(withPlayers: false)).Value!.Players);
        var online = (await Ops.OnlineAsync(withPlayers: true)).Value!;
        Assert.Equal(3, online.Count);
        var names = online.Players!.ToDictionary(p => p.Id, p => p.Name);
        Assert.Equal("Alice", names[_alice.ToString()]);
        Assert.Equal("Gen_Bob", names[_bob.ToString()]);
        Assert.Equal("Unknown", names["ghost"]);
        Assert.Equal("queued", online.Players!.Single(p => p.Id == _alice.ToString()).Status);
    }

    [SkippableFact]
    public async Task MatchesMergeGamesAndSetsAndLeaveOutCustomGames()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var matches = (await Ops.MatchesAsync()).Value!;
        Assert.Equal(["s1", "s2"], matches.Select(m => m.SetId).Order());

        var s1 = matches.Single(m => m.SetId == "s1");
        Assert.Equal("m1", s1.MatchId);
        Assert.Equal("1v1", s1.Mode);
        Assert.Equal([1, 0], s1.Scores);
        Assert.Equal("character_shaggy", s1.Teams["0"].Single().Character);
        Assert.Equal("character_batman", s1.Teams["1"].Single().Character);

        var s2 = matches.Single(m => m.SetId == "s2");
        Assert.True(s2.Conceded);
        Assert.Equal(2, s2.GamesPlayed);
    }

    [SkippableFact]
    public async Task APlayerIsFoundByIdNameInAnyCaseOrSteamIdAndAmbiguityIsRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Equal("Alice", (await Ops.FindPlayerAsync(_alice.ToString())).Value!.Name);
        Assert.Equal(_alice.ToString(), (await Ops.FindPlayerAsync("aLiCe")).Value!.Id);
        Assert.Equal(_alice.ToString(), (await Ops.FindPlayerAsync("76561190000000001")).Value!.Id);
        Assert.True((await Ops.FindPlayerAsync(_alice.ToString())).Value!.Online);

        var dup = await Ops.FindPlayerAsync("dup");
        Assert.NotNull(dup.Error);
        Assert.Contains(_dup1.ToString(), dup.Error);
        Assert.True((await Ops.FindPlayerAsync("nobody")).NotFound);
    }

    [SkippableFact]
    public async Task RenamingUpdatesMongoAndTheLiveConnection()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var renamed = await Ops.RenamePlayerAsync("Alice", "  Alicia  ");
        Assert.Null(renamed.Error);
        Assert.Equal("Alicia", renamed.Value!.Name);
        Assert.Equal("Alicia", (await Players.Find(new BsonDocument("_id", _alice)).SingleAsync())["name"].AsString);
        Assert.Equal("Alicia", (string?)await Redis.HashGetAsync($"connections:{_alice}", "username"));
    }

    [SkippableFact]
    public async Task RenamingSomeoneOfflineCreatesNoConnectionHash()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Redis.KeyDeleteAsync($"connections:{_bob}");
        Assert.Null((await Ops.RenamePlayerAsync(_bob.ToString(), "Robert")).Error);
        Assert.False(await Redis.KeyExistsAsync($"connections:{_bob}"));
    }

    [SkippableTheory]
    [InlineData("ALICE", "taken")]
    [InlineData("   ", "blank")]
    public async Task ARenameTheRulesForbidIsRefused(string name, string why)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var result = await Ops.RenamePlayerAsync("bob", name);
        Assert.Contains(why, result.Error);
        Assert.Equal("bob", (await Players.Find(new BsonDocument("_id", _bob)).SingleAsync())["name"].AsString);
    }

    [SkippableFact]
    public async Task ALongNameIsCutTo24Characters()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var result = await Ops.RenamePlayerAsync("bob", new string('x', 30));
        Assert.Equal(new string('x', 24), result.Value!.Name);
    }
}
