using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Friends;

/// <summary>
/// The friend writes the game and the OpenVersus client send (the TS acceptFriendRequest, declineFriendRequest,
/// blockPlayer, removeFriend): the request's status, both lists, the blocker's blocked players. Real Redis (database 15)
/// and Mongo (a database of its own, dropped): OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class FriendRequestsTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_friend_requests_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IFriendRequests Requests => _app!.Services.GetRequiredService<IFriendRequests>();
    private IMongoCollection<BsonDocument> Players => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");
    private IMongoCollection<BsonDocument> Lists => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(FriendsService.ListsCollection);
    private IMongoCollection<BsonDocument> FriendRequestDocs => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(FriendsService.RequestsCollection);
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
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("rankedtest", "TEST_PORT", 1, 1),
        [
            "--TEST_PORT=0", "--Control:Port=0", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddFriendRequests();
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
                await Redis.KeyDeleteAsync([new RedisKey($"connections:{id}"), new RedisKey($"player:{id}:blocked"), new RedisKey($"dll_notifications:{id}")]);
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task<List<(string Id, string Status)>> FriendsOfAsync(string id) =>
        (await Lists.Find(new BsonDocument("accountId", id)).FirstOrDefaultAsync())?["friends"].AsBsonArray
            .Select(e => (e["friendAccountId"].AsString, e["status"].AsString)).ToList() ?? [];

    private async Task<string> StatusAsync(string requestId) =>
        (await FriendRequestDocs.Find(new BsonDocument("_id", ObjectId.Parse(requestId))).FirstAsync())["status"].AsString;

    [SkippableFact]
    public async Task AcceptThenRemoveThenBlockAndDecline()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string a = NewPlayer(), b = NewPlayer(), c = NewPlayer();
        await Players.InsertManyAsync([new BsonDocument { { "_id", ObjectId.Parse(a) }, { "name", "A" } }, new BsonDocument { { "_id", ObjectId.Parse(b) }, { "name", "B" } }, new BsonDocument { { "_id", ObjectId.Parse(c) }, { "name", "C" } }]);
        string request = (await Requests.SendAsync(a, "A", b, "B", default)).RequestId!;

        // Only the recipient accepts; an unknown id and a settled request are refused as the TS code refused them.
        Assert.Equal("not_recipient", (await Requests.AcceptAsync(request, a, default)).Error);
        Assert.Equal("request_not_found", (await Requests.AcceptAsync("not-an-id", b, default)).Error);
        Assert.True((await Requests.AcceptAsync(request, b, default)).Success);
        Assert.Equal("request_not_pending", (await Requests.AcceptAsync(request, b, default)).Error);
        Assert.Equal([(b, "active")], await FriendsOfAsync(a));
        Assert.Equal([(a, "active")], await FriendsOfAsync(b));

        await Requests.RemoveAsync(a, b, default);
        Assert.Empty(await FriendsOfAsync(a));
        Assert.Empty(await FriendsOfAsync(b));

        // A blocks C while C's request to A is pending: the request is declined, C is a blocked entry of A's list and in A's blocked players.
        string pending = (await Requests.SendAsync(c, "C", a, "A", default)).RequestId!;
        Assert.Equal("cannot_block_self", (await Requests.BlockAsync(a, a, "A", default)).Error);
        Assert.True((await Requests.BlockAsync(a, c, "C", default)).Success);
        Assert.Equal("declined", await StatusAsync(pending));
        Assert.Equal([(c, "blocked")], await FriendsOfAsync(a));
        Assert.Equal([c], (await Players.Find(new BsonDocument("_id", ObjectId.Parse(a))).FirstAsync())["blockedPlayers"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal($"[\"{c}\"]", (string?)await Redis.StringGetAsync($"player:{a}:blocked"));
        Assert.Equal("blocked_by_target", (await Requests.SendAsync(c, "C", a, "A", default)).Error);

        // Unblock = remove: the entry and the blocked player go.
        await Requests.RemoveAsync(a, c, default);
        Assert.Empty(await FriendsOfAsync(a));
        Assert.Equal("[]", (string?)await Redis.StringGetAsync($"player:{a}:blocked"));

        // Decline: only the recipient, once.
        string again = (await Requests.SendAsync(c, "C", a, "A", default)).RequestId!;
        Assert.Equal("not_recipient", (await Requests.DeclineAsync(again, c, default)).Error);
        Assert.True((await Requests.DeclineAsync(again, a, default)).Success);
        Assert.Equal("declined", await StatusAsync(again));
        Assert.Equal("request_not_pending", (await Requests.DeclineAsync(again, a, default)).Error);
        Assert.Null(await Requests.IdByPublicIdAsync("nobody", default));
    }
}
