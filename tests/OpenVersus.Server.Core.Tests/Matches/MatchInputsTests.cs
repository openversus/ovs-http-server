using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// /ovs_match_inputs (<see cref="IMatchInputs"/>). Real Redis, database 15 (OVS_TEST_REDIS), and Mongo (a database of
/// its own, dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchInputsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_match_inputs_tests";
    private const string Match = "0000000000000000000e0100";
    private const string Human = "0000000000000000000e0001";
    private const string Other = "0000000000000000000e0002";
    private const string Key = "the-update-key";
    private const string MatchKey = "bWF0Y2gta2V5";

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;

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
        await CleanAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private Task CleanAsync() => Db.KeyDeleteAsync([Match, $"match:{Match}", $"connections:{Human}", $"connections:{Other}"]);

    private IMongoCollection<BsonDocument> Stored => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(MatchInputs.Collection);

    private IMatchInputs Inputs(string configuredKey = Key) => new MatchInputs(
        new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(_mongo!.GetDatabase(TestMongoDb)).BuildServiceProvider(),
        new TestOptions<RollbackSettings>(new RollbackSettings { MatchUpdateKey = configuredKey }), TimeProvider.System, NullLogger<MatchInputs>.Instance);

    // As the rollback server encodes it: one little-endian uint32 per frame, gzipped.
    private static string Encode(params uint[] frames)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (uint f in frames)
            {
                gzip.Write(BitConverter.GetBytes(f));
            }
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static uint[] Decode(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);
        byte[] all = raw.ToArray();
        return [.. Enumerable.Range(0, all.Length / 4).Select(i => BitConverter.ToUInt32(all, i * 4))];
    }

    private static JsonObject Body(string key = MatchKey) => new()
    {
        ["matchId"] = Match, ["key"] = key, ["endedBy"] = "AllPlayersDisconnected",
        ["startedAtUtc"] = "2026-10-02T18:00:00.0000000Z", ["endedAtUtc"] = "2026-10-02T18:03:00.0000000Z",
        ["frameRate"] = 60, ["durationFrames"] = 36000, ["droppedInputs"] = 3,
        ["players"] = new JsonArray(
            new JsonObject
            {
                ["playerIndex"] = 0, ["playerId"] = Human, ["playerName"] = "human", ["playerCharacter"] = "character_jason",
                ["frames"] = 4, ["receivedFrames"] = 3, ["encoding"] = "u32le-gzip", ["inputs"] = Encode(0, 8, 0, 8),
                ["missing"] = new JsonArray(new JsonArray(2, 2)),
            },
            new JsonObject
            {
                ["playerIndex"] = 1, ["playerId"] = Other, ["playerName"] = "other", ["playerCharacter"] = "character_shaggy",
                ["frames"] = 2, ["receivedFrames"] = 2, ["encoding"] = "u32le-gzip", ["inputs"] = Encode(5, 5), ["missing"] = new JsonArray(),
            }),
    };

    // The notification MatchLauncher / the matchmaker writes under the bare match id.
    private Task NotificationAsync() => Db.StringSetAsync(Match, Js.Stringify(new JsonObject
    {
        ["players"] = new JsonArray(
            new JsonObject { ["playerId"] = Human, ["playerIndex"] = 0, ["teamIndex"] = 0 },
            new JsonObject { ["playerId"] = Other, ["playerIndex"] = 1, ["teamIndex"] = 1 }),
        ["matchId"] = Match, ["matchKey"] = MatchKey, ["map"] = "M004", ["mode"] = "1v1",
    }));

    [SkippableTheory]
    [InlineData(null, Key)]
    [InlineData("wrong-key-of-same", Key)]
    [InlineData(Key, "")]
    [InlineData("MisconfiguredMatchUpdateKey", "MisconfiguredMatchUpdateKey")]
    public async Task WithoutTheConfiguredKeyNothingIsStored(string? sent, string configured)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var (status, answer) = await Inputs(configured).StoreAsync(sent, Body(), default);
        Assert.Equal(403, status);
        Assert.Equal("""{"error":"Invalid signature"}""", answer.ToJsonString());
        Assert.Equal(0, await Stored.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [SkippableFact]
    public async Task EachPlayerIsStoredWithTheMatchsContext()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await NotificationAsync();
        await Db.StringSetAsync($"match:{Match}", """{"matchId":"0000000000000000000e0100","matchType":"1v1"}""");
        await Db.HashSetAsync($"connections:{Human}", "GameplayPreferences", "42");

        // The key compares as the TS server compares it: ignoring case.
        var (status, answer) = await Inputs().StoreAsync(Key.ToUpperInvariant(), Body(), default);
        Assert.Equal(200, status);
        Assert.Equal("""{"status":"ok"}""", answer.ToJsonString());

        var human = await Stored.Find(new BsonDocument { { "matchId", Match }, { "playerIndex", 0 } }).SingleAsync();
        Assert.Equal(Human, human["playerId"].AsString);
        Assert.Equal("character_jason", human["character"].AsString);
        Assert.Equal(0, human["teamIndex"].AsInt32);
        Assert.Equal("M004", human["map"].AsString);
        Assert.Equal("1v1", human["mode"].AsString);
        Assert.Equal("1v1", human["matchType"].AsString);
        Assert.Equal(42, human["gameplayPreferences"].AsInt32);
        Assert.Equal("AllPlayersDisconnected", human["endedBy"].AsString);
        Assert.Equal(new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc), human["startedAt"].ToUniversalTime());
        Assert.Equal(4, human["frames"].ToInt64());
        Assert.Equal(3, human["receivedFrames"].ToInt64());
        Assert.Equal(3, human["droppedInputs"].ToInt64());
        Assert.Equal([0u, 8u, 0u, 8u], Decode(human["inputs"].AsBsonBinaryData.Bytes));
        Assert.Equal(2, human["missing"][0][1].ToInt64());

        var other = await Stored.Find(new BsonDocument { { "matchId", Match }, { "playerIndex", 1 } }).SingleAsync();
        Assert.Equal(1, other["teamIndex"].AsInt32);
        Assert.Equal([5u, 5u], Decode(other["inputs"].AsBsonBinaryData.Bytes));
        // No session settings and no player record: unknown, not a default.
        Assert.Equal(BsonNull.Value, other["gameplayPreferences"]);
    }

    [SkippableFact]
    // The shutdown path and the normal end can both reach the server: one document per player, the latest.
    public async Task SentAgainReplaces()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Inputs().StoreAsync(Key, Body(), default);
        var again = Body();
        again["endedBy"] = "Shutdown";
        await Inputs().StoreAsync(Key, again, default);
        Assert.Equal(2, await Stored.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal("Shutdown", (await Stored.Find(new BsonDocument("playerIndex", 0)).SingleAsync())["endedBy"].AsString);
    }

    [SkippableFact]
    public async Task AKeyOtherThanTheMatchsIsRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await NotificationAsync();
        var (status, _) = await Inputs().StoreAsync(Key, Body("another-match-key"), default);
        Assert.Equal(400, status);
        Assert.Equal(0, await Stored.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [SkippableFact]
    // The notification lasts 20 minutes; the recording is kept without the context it held.
    public async Task AMatchNoLongerInRedisIsStoredWithoutItsContext()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var (status, _) = await Inputs().StoreAsync(Key, Body(), default);
        Assert.Equal(200, status);
        var human = await Stored.Find(new BsonDocument("playerIndex", 0)).SingleAsync();
        Assert.Equal(BsonNull.Value, human["map"]);
        Assert.Equal(BsonNull.Value, human["teamIndex"]);
    }
}
