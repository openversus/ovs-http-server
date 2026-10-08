using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Bans;

/// <summary>
/// A person ban against a real Redis (database 15, OVS_TEST_REDIS) and Mongo (a database of its own, dropped:
/// OVS_TEST_MONGO): every identifier banned, in effect at once, recorded in Mongo and the auto-ban file, the
/// connection cut; and the login's order: no account for a banned identifier, a banned name bans, a force-change name
/// is renamed before the session is written.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class PersonBansTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_ban_tests";
    private const string Secret = "ban-tests-secret-0123456789abcdef0123456789";
    private const string Steam = "76561198000000077", Epic = "ABCDEF0123456789ABCDEF0123456789", Install = "0123456789abcdef0123456789abcdef";
    private const string Hardware = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-person-bans-").FullName;
    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private BanSettings _settings = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _redis!.GetDatabase();

    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private IMongoCollection<BsonDocument> Players => Mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);

    public async Task InitializeAsync()
    {
        _settings = new BanSettings
        {
            IpFile = null, CidrFile = null, EpicIdFile = null, HardwareFile = null, InstallIdFile = null,
            SteamIdFile = Path.Combine(_dir, "steamid_bans.txt"),
            AutoBansFile = Path.Combine(_dir, "auto_bans.yaml"),
            BannedNamesFile = Path.Combine(_dir, "banned_names.yaml"),
            ForceChangeNamesFile = Path.Combine(_dir, "force_change_names.yaml"),
            AllowedNamesFile = null,
        };
        File.WriteAllText(_settings.BannedNamesFile, "terms:\n  - 'zorb'\n");
        File.WriteAllText(_settings.ForceChangeNamesFile, "terms:\n  - 'blat'\n");
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        _redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } },
            User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER"),
            Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW"),
            DefaultDatabase = TestRedisDb,
        });
        _mongo = new MongoClient(new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl());
        await _mongo.DropDatabaseAsync(TestMongoDb);
        await ClearRedisAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await ClearRedisAsync();
            _redis.Dispose();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private async Task ClearRedisAsync()
    {
        foreach (var server in _redis!.GetServers())
        {
            await foreach (var key in server.KeysAsync(TestRedisDb, "*"))
            {
                await Redis.KeyDeleteAsync(key);
            }
        }
    }

    private IServiceProvider Stores() => new ServiceCollection()
        .AddSingleton<IConnectionMultiplexer>(_redis!)
        .AddSingleton(Mongo)
        .BuildServiceProvider();

    private PersonBans Bans() => new(Stores(), new TestOptions<BanSettings>(_settings), TimeProvider.System, NullLogger<PersonBans>.Instance);

    private AccessService Access()
    {
        var options = new TestOptions<BanSettings>(_settings);
        var services = Stores();
        return new AccessService(services, new TestOptions<AccessSettings>(new AccessSettings { JwtSecret = Secret }), new TestOptions<RealtimeSettings>(new RealtimeSettings()),
            new TestOptions<SeasonSettings>(new SeasonSettings()), new BanService(services, options, TimeProvider.System, NullLogger<BanService>.Instance),
            new NameRules(options, TimeProvider.System, NullLogger<NameRules>.Instance), Bans(), TimeProvider.System, NullLogger<AccessService>.Instance);
    }

    // The identify token the client sends to /access (the login reads the identity from its claims).
    private static string Identified(string steam) =>
        AccessTokens.Sign(new JsonObject { ["steamId"] = steam, ["installId"] = Install, ["identityRegistered"] = "1" }, Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

    private async Task<ObjectId> SeedAsync(string name, string hydraUsername = "OpenVersus_1234567890123", string quality = "strong")
    {
        var id = ObjectId.GenerateNewId();
        await Players.InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "name", name }, { "hydraUsername", hydraUsername }, { "ip", "198.51.100.40" },
            { "steamId", Steam }, { "epicId", Epic }, { "hardwareId", Hardware }, { "hardwareIdVersion", "2" }, { "hardwareIdQuality", quality },
            { "installId", Install }, { "account", new BsonDocument("current_ip", "198.51.100.41") },
        });
        return id;
    }

    [SkippableFact]
    public async Task BansEveryIdentifierRecordsItAndCutsTheConnection()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Someone");
        string player = id.ToString();
        await Redis.SetAddAsync("online_players", player);
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _redis!.GetSubscriber().SubscribeAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), (_, v) => heard.TrySetResult(v.ToString()));
        // Something must hear the disconnect for it to count as delivered.
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(GatewayChannels.Disconnect), (_, _) => { });

        var record = await Bans().BanAsync(new BanRequest(player, "banned name", "namechange", AttemptedName: "zorb", MatchedList: "banned_names", MatchedTerm: "zorb",
            RequestIp: "198.51.100.42", UserAgent: "test agent"));

        var expected = new BanIdentifiers("198.51.100.42", Steam, Epic.ToLowerInvariant(), Hardware, Install, player);
        Assert.NotNull(record);
        Assert.Equal(expected, record.Who);
        Assert.Equal(("Someone", true, true), (record.NameAtBan, record.Online, record.Disconnected));
        foreach (var (kind, value) in expected.Known())
        {
            Assert.True(await Redis.SetContainsAsync(BanService.Key(kind), value), $"{kind} {value}");
        }

        Assert.Equal(player, await heard.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var logged = await Redis.StreamRangeAsync(PlayerMessages.LogKey(player));
        Assert.Contains(logged, e => e.Values.Any(v => v.Name == "disconnect"));

        var stored = await Mongo.GetCollection<BsonDocument>(PersonBans.Collection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(expected, PersonBans.IdentifiersOf(stored));
        Assert.Equal(("namechange", "zorb", "zorb", "test agent"), (stored["source"].AsString, stored["player"]["attempted_name"].AsString,
            stored["matched"]["term"].AsString, stored["request"]["user_agent"].AsString));
        Assert.Equal([expected], AutoBans.Parse(File.ReadAllText(_settings.AutoBansFile!)));
    }

    [SkippableFact]
    public async Task EitherRecordAloneRestoresTheBansInRedis()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = (await SeedAsync("Someone")).ToString();
        await Bans().BanAsync(new BanRequest(player, "banned by an administrator", "manual"));

        await ClearRedisAsync();
        await BanLoader.LoadAsync(Mongo, Redis, new BanSettings { AutoBansFile = null }, NullLogger.Instance, default);
        Assert.True(await Redis.SetContainsAsync(BannedPlayers.Key, player));
        Assert.True(await Redis.SetContainsAsync("bans:steam", Steam));

        await ClearRedisAsync();
        await BanLoader.LoadAsync(null, Redis, _settings, NullLogger.Instance, default);
        Assert.True(await Redis.SetContainsAsync(BannedPlayers.Key, player));
        Assert.True(await Redis.SetContainsAsync("bans:hardware", Hardware));
    }

    [SkippableFact]
    public async Task AWeakHardwareHashIsNotBanned()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = (await SeedAsync("Someone", quality: "weak")).ToString();
        var record = await Bans().BanAsync(new BanRequest(player, "banned by an administrator", "manual"));
        Assert.Equal("", record!.Who.HardwareId);
        Assert.Equal(0, await Redis.SetLengthAsync("bans:hardware"));
    }

    [SkippableFact]
    public async Task NoPlayerNoBan()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        Assert.Null(await Bans().BanAsync(new BanRequest(ObjectId.GenerateNewId().ToString(), "x", "manual")));
        Assert.Null(await Bans().BanAsync(new BanRequest("not an id", "x", "manual")));
        Assert.False(File.Exists(_settings.AutoBansFile));
        Assert.Equal(0, await Redis.SetLengthAsync(BannedPlayers.Key));
    }

    private BanSweep Sweep()
    {
        var options = new TestOptions<BanSettings>(_settings);
        var services = Stores();
        return new BanSweep(services, options, new BanService(services, options, TimeProvider.System, NullLogger<BanService>.Instance), Bans(),
            new OpenVersus.Server.Core.Hosting.ServiceInstance(), TimeProvider.System, NullLogger<BanSweep>.Instance);
    }

    [SkippableFact]
    public async Task TheSweepCutsOffWhoeverIsOnlineThroughABannedIdentifier()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var matched = await SeedAsync("Shares a banned Steam id");
        var already = ObjectId.GenerateNewId();
        var clean = ObjectId.GenerateNewId();
        await Players.InsertManyAsync([new BsonDocument { { "_id", already }, { "name", "Banned before" } }, new BsonDocument { { "_id", clean }, { "name", "Nobody" } }]);
        await Redis.SetAddAsync("online_players", [matched.ToString(), already.ToString(), clean.ToString()]);
        await Redis.SetAddAsync(BannedPlayers.Key, already.ToString());
        File.WriteAllText(_settings.SteamIdFile!, Steam + "\n");

        Assert.Equal(1, await Sweep().SweepAsync(Redis, default));

        Assert.True(await Redis.SetContainsAsync(BannedPlayers.Key, matched.ToString()));
        // Only the player id: their other identifiers are not banned through this match.
        Assert.Equal(0, await Redis.SetLengthAsync("bans:epic"));
        Assert.Equal(0, await Redis.SetLengthAsync("bans:hardware"));
        var stored = await Mongo.GetCollection<BsonDocument>(PersonBans.Collection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal((matched.ToString(), "sweep", "steamid_bans.txt", Steam), (stored["player"]["id"].AsString, stored["source"].AsString,
            stored["matched"]["list"].AsString, stored["matched"]["term"].AsString));
        // Cut off again: banned before, still online.
        Assert.NotEmpty(await Redis.StreamRangeAsync(PlayerMessages.LogKey(matched.ToString())));
        Assert.NotEmpty(await Redis.StreamRangeAsync(PlayerMessages.LogKey(already.ToString())));
        Assert.Empty(await Redis.StreamRangeAsync(PlayerMessages.LogKey(clean.ToString())));
    }

    [SkippableFact]
    public async Task OneReplicaSweepsAtATime()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var matched = await SeedAsync("Shares a banned Steam id");
        await Redis.SetAddAsync("online_players", matched.ToString());
        File.WriteAllText(_settings.SteamIdFile!, Steam + "\n");
        await Redis.StringSetAsync("bans:sweep:lock", "another replica");

        Assert.Equal(0, await Sweep().SweepAsync(Redis, default));
        Assert.False(await Redis.SetContainsAsync(BannedPlayers.Key, matched.ToString()));
        Assert.Equal("another replica", (string?)await Redis.StringGetAsync("bans:sweep:lock"));
    }

    [SkippableFact]
    public async Task ALoginWithABannedIdentifierMakesNoAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        File.WriteAllText(_settings.SteamIdFile!, Steam + "\n");
        // Another account holds the install id under its own Steam id: looking an account up for this login would take
        // the install id from it, so the banned identifier is refused before the lookup.
        var other = ObjectId.GenerateNewId();
        await Players.InsertOneAsync(new BsonDocument { { "_id", other }, { "name", "Household" }, { "steamId", "76561198000000079" }, { "installId", Install } });
        Assert.IsType<AccessResult.Banned>(await Access().LoginAsync("198.51.100.50", Identified(Steam)));
        Assert.Equal(1, await Players.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(Install, (await Players.Find(new BsonDocument("_id", other)).SingleAsync())["installId"].AsString);

        Assert.IsType<AccessResult.Ok>(await Access().LoginAsync("198.51.100.50", Identified("76561198000000078")));
    }

    [SkippableFact]
    public async Task ALoginAsABannedPlayerIsRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Someone");
        await Redis.SetAddAsync(BannedPlayers.Key, id.ToString());
        Assert.IsType<AccessResult.Banned>(await Access().LoginAsync("198.51.100.50", Identified(Steam)));
    }

    [SkippableFact]
    public async Task ALoginWithABannedNameBansThePerson()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("my zorb name");
        Assert.IsType<AccessResult.Banned>(await Access().LoginAsync("198.51.100.50", Identified(Steam)));
        var stored = await Mongo.GetCollection<BsonDocument>(PersonBans.Collection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal((id.ToString(), "login", "my zorb name", "zorb", "198.51.100.50"),
            (stored["player"]["id"].AsString, stored["source"].AsString, stored["player"]["name_at_ban"].AsString, stored["matched"]["term"].AsString, stored["identifiers"]["ip"].AsString));
        Assert.True(await Redis.SetContainsAsync("bans:steam", Steam));
    }

    [SkippableTheory]
    [InlineData("OpenVersus_1234567890123", true)]
    [InlineData("SomeoneElse", false)]
    public async Task ALoginWithAForceChangeNameIsRenamed(string hydraUsername, bool keepsOwn)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("the blat", hydraUsername);
        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync("198.51.100.50", Identified(Steam)));
        Assert.Equal(id.ToString(), result.PlayerId);
        string name = (await Players.Find(new BsonDocument("_id", id)).SingleAsync())["name"].AsString;
        if (keepsOwn)
        {
            Assert.Equal(hydraUsername, name);
        }
        else
        {
            Assert.Matches("^OpenVersus_[0-9]{13}$", name);
        }

        // The session carries the new name from the login itself.
        Assert.Equal(name, (string?)await Redis.HashGetAsync($"connections:{id}", "username"));
        Assert.Equal(0, await Mongo.GetCollection<BsonDocument>(PersonBans.Collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
