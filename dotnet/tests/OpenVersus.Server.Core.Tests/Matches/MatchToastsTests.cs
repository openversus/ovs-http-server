using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// toast_player (<see cref="IMatchToasts"/>). Real Redis, database 15 (OVS_TEST_REDIS), and Mongo (a database of its own,
/// dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchToastsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_match_toast_tests";
    private const string Match = "0000000000000000000d0100";
    private const string Toaster = "0000000000000000000d0001";
    private const string Toastee = "0000000000000000000d0002";

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private readonly ConcurrentQueue<string> _published = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        // Channels ignore the database: only this class's toastee counts.
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains(Toastee, StringComparison.Ordinal))
            {
                _published.Enqueue(m.ToString());
            }
        });
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private IMongoCollection<BsonDocument> Counters => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playercounters");

    private IMatchToasts Toasts() => new MatchToasts(
        new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(_mongo!.GetDatabase(TestMongoDb)).BuildServiceProvider(),
        TimeProvider.System, NullLogger<MatchToasts>.Instance);

    private static JsonObject Body() => new() { ["ContainerMatchId"] = Match, ["ToasteeId"] = Toastee };

    private async Task<List<string>> PublishedAsync()
    {
        await Task.Delay(200);
        return [.. _published];
    }

    private async Task<BsonDocument?> CountersOfAsync(string id) => await Counters.Find(new BsonDocument("accountId", id)).FirstOrDefaultAsync();

    [SkippableFact]
    public async Task TheToasterPaysOneTheToasteeGetsTwoAndIsShownTheToast()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Toasts().ToastAsync(Toaster, "toaster name", Body(), default);

        // getCounters creates the document as mongoose does (defaults, version key, timestamps), then the spend.
        var doc = await CountersOfAsync(Toaster);
        Assert.Equal(99, doc!["match_toasts"].ToInt64());
        Assert.Equal(0, doc["lastToastBonusUnix"].ToInt64());
        Assert.Equal(0, doc["__v"].AsInt32);
        Assert.True(doc.Contains("createdAt") && doc.Contains("updatedAt"));
        // The toastee's document made the same way, then +2 on its default 100.
        var toastee = await CountersOfAsync(Toastee);
        Assert.Equal(102, toastee!["match_toasts"].ToInt64());
        Assert.True(toastee.Contains("updatedAt"));
        Assert.Equal([$$$"""{"playerIds":["{{{Toastee}}}"],"message":{"data":{"template_id":"ToastReceivedNotification","ToasterAccountID":"{{{Toaster}}}","RewardsGranted":[{"RewardGuid":"OVS-TOAST-TOAST-0001","Constraints":[],"RewardGrantMethod":"DirectInventoryItem","InventoryHsda":"match_toasts","DirectInventoryItemCount":2}]},"payload":{"frm":{"id":"internal-server","type":"server-api-key"},"template":"realtime","account_id":"{{{Toastee}}}","profile_id":"{{{Toastee}}}"},"header":"","cmd":"profile-notification"}}"""],
            await PublishedAsync());
    }

    [SkippableFact]
    public async Task AToasterWithNoneLeftStaysAtZeroAndTheToastIsStillSent()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Counters.InsertOneAsync(new BsonDocument { { "accountId", Toaster }, { "match_toasts", 0 }, { "lastToastBonusUnix", 0 }, { "__v", 0 } });
        await Toasts().ToastAsync(Toaster, "toaster name", Body(), default);
        Assert.Equal(0, (await CountersOfAsync(Toaster))!["match_toasts"].ToInt64());
        Assert.Single(await PublishedAsync());
    }

    [SkippableFact]
    // A grant that failed (here: no Mongo at all) shows no toast: its popup would not match the toastee's inventory. The
    // spend failing alone does not stop it (its error is caught and logged), as there.
    public async Task NoToastIsShownWhenTheGrantFails()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var noMongo = new MatchToasts(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
            TimeProvider.System, NullLogger<MatchToasts>.Instance);
        await noMongo.ToastAsync(Toaster, "toaster name", Body(), default);
        Assert.Empty(await PublishedAsync());
    }

    [SkippableTheory]
    [InlineData("ContainerMatchId")]
    [InlineData("ToasteeId")]
    public async Task AMissingFieldDoesNothing(string missing)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var body = Body();
        body.Remove(missing);
        await Toasts().ToastAsync(Toaster, "toaster name", body, default);
        Assert.Null(await CountersOfAsync(Toaster));
        Assert.Null(await CountersOfAsync(Toastee));
        Assert.Empty(await PublishedAsync());
    }
}
