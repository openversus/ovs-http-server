using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Friends;

/// <summary>
/// Where the friends reads deliberately differ from the TS server (tools/friends/friends_diff.mjs checks where they must
/// not). Real Redis (database 15) and Mongo (a database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER,
/// OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class FriendsServiceTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_friends_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IFriendsService Friends => _app!.Services.GetRequiredService<IFriendsService>();
    private IMongoCollection<BsonDocument> Lists => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(FriendsService.ListsCollection);

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);


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
        // The TS model's index (mongoose creates it): one list per account.
        await Lists.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("accountId", 1), new CreateIndexOptions { Unique = true }));

        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("friendstest", "TEST_PORT", 1, 1),
        [
            "--TEST_PORT=0", "--Control:Port=0", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddFriends();
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

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    [SkippableTheory]
    // An /api/identify token's id, and one that is not an ObjectId: the TS server creates a friend list for either.
    [InlineData("")]
    [InlineData("not-a-player")]
    public async Task ASessionThatIsNotAPlayerWritesNothing(string accountId)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Equal("""{"total":0,"page":1,"page_size":20,"results":[]}""", (await Friends.FriendsAsync(accountId)).Json);
        // The TS findById throws on such an id, and its catch answers.
        Assert.Equal("""{"total":0,"page":1,"page_size":1000,"results":[]}""", (await Friends.BlockedAsync(accountId)).Json);
        Assert.Equal(0, await Lists.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [SkippableFact]
    public async Task FirstRequestsAtOnceMakeOneListAndAllAnswer()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        var pages = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Friends.FriendsAsync(id)));
        // The TS server answers the losers of the race from its catch (page_size 1000).
        Assert.All(pages, p => Assert.Equal("""{"total":0,"page":1,"page_size":20,"results":[]}""", p.Json));
        Assert.Equal(1, await Lists.CountDocumentsAsync(new BsonDocument("accountId", id)));
    }

    [Fact]
    public async Task NoSessionIsTheTsCatchsAnswer()
    {
        // No stores are needed to get there: the TS handler throws decoding a missing token before it reads anything.
        IFriendsService friends = new FriendsService(new ServiceCollection().BuildServiceProvider(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FriendsService>.Instance);
        foreach (var page in new[] { await friends.FriendsAsync(null), await friends.BlockedAsync(null), await friends.IncomingAsync(null), await friends.OutgoingAsync(null) })
        {
            Assert.Equal("""{"total":0,"page":1,"page_size":1000,"results":[]}""", page.Json);
        }
    }
}
