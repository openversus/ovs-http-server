using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Rifts;
using StackExchange.Redis;

namespace OpenVersus.Server.MatchFlow.Tests;

/// <summary>
/// A match result recorded by the services the subscribers call, as this executable registers them: some of what they
/// need is looked up only when a result arrives (the reward tracks), which building the host does not check. Real Redis
/// (database 14) and Mongo (a database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW,
/// OVS_TEST_MONGO. The services are called directly: a result published on the channel would also reach whatever else
/// subscribes on that Redis (channels ignore the database number).
/// </summary>
public sealed class MatchResultTests : IAsyncLifetime
{
    private const int TestRedisDb = 14;
    private const string TestMongoDb = "ovs_matchflow_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly string _player = ObjectId.GenerateNewId().ToString();
    private readonly string _match = Guid.NewGuid().ToString();
    private readonly string _riftMatch = Guid.NewGuid().ToString();
    private Factory? _factory;
    private IMongoDatabase? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _factory!.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private sealed class Factory(string mongoUri) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            string[] parts = s_redis!.Split(':');
            builder.UseSetting("REDIS", parts[0]);
            builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
            builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
            builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
            builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
            builder.UseSetting("MONGODB_URI", mongoUri);
            builder.UseSetting("RewardTracks:CharacterMastery", "true");
        }
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        var url = new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl();
        _mongo = new MongoClient(url).GetDatabase(TestMongoDb);
        await _mongo.Client.DropDatabaseAsync(TestMongoDb);
        _factory = new Factory(url.ToString());
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await Redis.KeyDeleteAsync([_match, $"match_xp:{_match}:{_player}", $"player:{_player}",
                $"rift_match:{_riftMatch}", $"rift_match:{_riftMatch}:recorded"]);
            await _factory.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.Client.DropDatabaseAsync(TestMongoDb);
        }
    }

    [SkippableFact]
    public async Task Match_xp_reaches_the_players_reward_tracks()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Equal(TestRedisDb, Redis.Database);
        await Redis.StringSetAsync(_match, $$"""{"mode":"1v1","players":[{"playerId":"{{_player}}","teamIndex":0}]}""");
        await Redis.HashSetAsync($"player:{_player}", [new("character", "character_shaggy"), new("skin", "skin_shaggy_default")]);

        await _factory!.Services.GetRequiredService<IMissionService>().RecordMatchXpAsync(_match, _player, 0, CancellationToken.None);

        var tracks = await _mongo!.GetCollection<BsonDocument>("rewardtracks").Find(new BsonDocument("_id", ObjectId.Parse(_player))).FirstOrDefaultAsync();
        Assert.NotNull(tracks);
    }

    [SkippableFact]
    public async Task A_rift_result_reaches_the_players_rift_data()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Equal(TestRedisDb, Redis.Database);
        await Redis.StringSetAsync($"rift_match:{_riftMatch}",
            $$"""{"playerId":"{{_player}}","slug":"rift_test","chapterId":"chapter","nodeId":"node","lobbyId":"lobby","difficulty":0}""");

        // A loss: the node is not won, the rift state still records the node as last played.
        await _factory!.Services.GetRequiredService<IRiftProgressService>().RecordResultAsync(_riftMatch, 1, null, CancellationToken.None);

        Assert.True(await Redis.KeyExistsAsync($"rift_match:{_riftMatch}:recorded"));
        Assert.Equal(1, await _mongo!.GetCollection<BsonDocument>("riftinstances").CountDocumentsAsync(new BsonDocument("account_id", _player)));
    }
}
