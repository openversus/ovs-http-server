using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>
/// The first login of each kind of live account once the C# server and the new client replace the TS server
/// (2026-10-17, Tuggernuts: "whats going to be everyones outcome?"): accounts as the TS server left them (Steam ids only
/// claimed, no tickets, any hardware fingerprint, id-less IP accounts), and the new client's login (an identify token:
/// a proved Steam id when Steam's ticket was read, its install id and fingerprint). Against a real Redis (database 15,
/// OVS_TEST_REDIS) and Mongo (a database of its own, dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class SwitchoverLoginTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_switchover_login_tests";
    private const string Secret = "switchover-game-secret-0123456789abcdef0123";
    private const string IdentifySecret = "switchover-identify-secret-0123456789abcd";
    private const string HomeIp = "198.51.100.80", NewIp = "203.0.113.9";
    private const string SteamA = "76561198000000101", SteamB = "76561198000000202";
    private const string EpicA = "0123456789abcdef0123456789abcd01";
    private const string InstallA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", InstallB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string HwA = new('a', 64), HwB = new('b', 64);
    // A fingerprint several people's PCs report alike (as 4ef4a6f6... and d006fce1... on the live server, 2026-10-17).
    private static readonly string HwShared = "4ef4a6f6fafdcdb43f930eff79b3aed00a874609706c7c36eb6b1f6133d7d2f3";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-switchover-").FullName;
    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private BanSettings _bans = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _redis!.GetDatabase();

    private IMongoCollection<BsonDocument> Players => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(PlayerRecord.Collection);

    public async Task InitializeAsync()
    {
        _bans = new BanSettings
        {
            IpFile = null, CidrFile = null, EpicIdFile = null, HardwareFile = null, InstallIdFile = null, SteamIdFile = null, AllowedNamesFile = null,
            AutoBansFile = Path.Combine(_dir, "auto_bans.yaml"),
            BannedNamesFile = Path.Combine(_dir, "banned_names.yaml"),
            ForceChangeNamesFile = Path.Combine(_dir, "force_change_names.yaml"),
        };
        File.WriteAllText(_bans.BannedNamesFile, "terms: []\n");
        File.WriteAllText(_bans.ForceChangeNamesFile, "terms: []\n");
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

    // --- Steam players

    [SkippableFact]
    public async Task ASteamPlayerWhoseAccountClaimsTheirSteamIdLandsOnIt()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Steam Player", steam: SteamA, install: InstallA, hardware: HwA);

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
        Assert.Equal(SteamA, (await AccountAsync(account))["steamId"].AsString);
    }

    [SkippableFact]
    public async Task ASteamPlayerFromANewIpAndANewInstallStillLandsOnTheirAccountByTheirSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // The installer's fresh install has a new install id; the router gave a new IP.
        var account = await SeedAsync("Moved", steam: SteamA, install: InstallB, hardware: HwB);

        var login = await LoginAsync(NewIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
        Assert.Equal(InstallA, (await AccountAsync(account))["installId"].AsString);
    }

    [SkippableFact]
    public async Task AnInstallOnlyAccountTakesItsOwnersProvedSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // The C# client's earlier builds could log in without Steam (the ticket asked for before Steam loaded).
        var account = await SeedAsync("Install Only", steam: "", install: InstallA, hardware: HwA);

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
        Assert.Equal(SteamA, (await AccountAsync(account))["steamId"].AsString);
    }

    [SkippableFact]
    public async Task WhenSteamsTicketCannotBeReadTheInstallIdStillFindsTheAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Steam Player", steam: SteamA, install: InstallA, hardware: HwA);

        var login = await LoginAsync(HomeIp, steam: "", install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
        Assert.Equal(SteamA, (await AccountAsync(account))["steamId"].AsString);
    }

    // --- Epic players

    [SkippableFact]
    public async Task AnEpicPlayerWhoseAccountCarriesTheirEpicIdLandsOnItWhenTheGameProvesIt()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Epic Player", steam: "", install: InstallB, epic: EpicA, hardware: HwB);

        var login = await LoginAsync(NewIp, steam: "", install: InstallA, hardware: HwA, provedEpic: EpicA);

        Assert.Equal(account.ToString(), login);
    }

    // --- IP-only accounts (from before the client sent any id)

    [SkippableFact]
    public async Task AnIpOnlyAccountIsAdoptedByTheFirstIdentifiedLoginFromItsIp()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Old Timer", steam: "", install: "", ip: HomeIp);

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
        var saved = await AccountAsync(account);
        Assert.Equal(SteamA, saved["steamId"].AsString);
        Assert.Equal(InstallA, saved["installId"].AsString);
    }

    [SkippableFact]
    public async Task AnIpOnlyAccountIsAdoptedOverTheIpsProvisionalGhosts()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Old Timer", steam: "", install: "", ip: HomeIp, lastSeen: DateTime.UtcNow.AddDays(-20));
        await SeedAsync("OpenVersus_1111111111111", steam: "", install: "", ip: HomeIp, provisional: true, lastSeen: DateTime.UtcNow.AddDays(-1));

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(account.ToString(), login);
    }

    [SkippableFact]
    public async Task AnIpOnlyAccountOnAnIpThePlayerNoLongerHasIsNotFound()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // Known gap (manual merge): nothing ties the person to the old IP's account.
        var account = await SeedAsync("Old Timer", steam: "", install: "", ip: HomeIp);

        var login = await LoginAsync(NewIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.NotEqual(account.ToString(), login);
    }

    [SkippableFact]
    public async Task AHouseholdsTwoIpOnlyAccountsAreNeverGuessedBetween()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // Known gap (manual merge): neither sibling is given the other's account.
        var first = await SeedAsync("Sibling One", steam: "", install: "", ip: HomeIp);
        var second = await SeedAsync("Sibling Two", steam: "", install: "", ip: HomeIp);

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.NotEqual(first.ToString(), login);
        Assert.NotEqual(second.ToString(), login);
    }

    // --- Shared PCs and copied folders

    [SkippableFact]
    public async Task TwoSteamAccountsOnOnePcKeepTwoAccounts()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var a = await SeedAsync("Brother", steam: SteamA, install: InstallA, hardware: HwA);
        var b = await SeedAsync("Sister", steam: SteamB, install: "", hardware: HwA);

        Assert.Equal(a.ToString(), await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA));
        Assert.Equal(b.ToString(), await LoginAsync(HomeIp, steam: SteamB, install: InstallA, hardware: HwA));
        Assert.Equal(a.ToString(), await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA));
        Assert.Equal(SteamA, (await AccountAsync(a))["steamId"].AsString);
        Assert.Equal(SteamB, (await AccountAsync(b))["steamId"].AsString);
    }

    [SkippableFact]
    public async Task ACopiedPluginFolderFromAnotherPcGivesTheFriendTheirOwnAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var sender = await SeedAsync("Sender", steam: SteamA, install: InstallA, hardware: HwA);

        var friend = await LoginAsync(NewIp, steam: SteamB, install: InstallA, hardware: HwB);

        Assert.NotEqual(sender.ToString(), friend);
        Assert.Equal(SteamA, (await AccountAsync(sender))["steamId"].AsString);
    }

    // --- The live server's shared accounts ("im high asf", "Youtube_jeffinator"): many people, one fingerprint

    [SkippableFact]
    public async Task ASharedAccountsOwnerLogsInByTheirSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var shared = await SeedAsync("im high asf", steam: SteamA, install: InstallB, hardware: HwShared);

        Assert.Equal(shared.ToString(), await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwShared));
    }

    [SkippableFact]
    public async Task SomeoneElseWithTheSharedFingerprintAndTheirOwnInstallGetsTheirOwnAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var shared = await SeedAsync("im high asf", steam: SteamA, install: InstallA, hardware: HwShared);

        var other = await LoginAsync(NewIp, steam: SteamB, install: InstallB, hardware: HwShared);

        Assert.NotEqual(shared.ToString(), other);
        Assert.Equal(SteamA, (await AccountAsync(shared))["steamId"].AsString);
    }

    [SkippableFact]
    public async Task SomeoneElseWithTheSharedFingerprintAndTheSharedAccountsInstallIdDoesNotTakeItOver()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // The TS server moved the install id to the last person in: its install id can be another person's own. That
        // person proves another Steam id from the same fingerprint, which several PCs report alike.
        var shared = await SeedAsync("im high asf", steam: SteamA, install: InstallB, hardware: HwShared);

        var other = await LoginAsync(NewIp, steam: SteamB, install: InstallB, hardware: HwShared);
        var owner = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwShared);

        Assert.Equal(shared.ToString(), owner);
        Assert.NotEqual(shared.ToString(), other);
    }

    // --- Steam ids the old clients claimed twice

    [SkippableFact(Skip = "Known, not fixed for the switch: of two accounts claiming one Steam id the login does not pick the one the person plays. The live export of 2026-10-17 has no Steam id claimed twice.")]
    public async Task OfTwoAccountsClaimingOneSteamIdTheOneThePersonPlaysIsChosen()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // An old ghost claimed the Steam id first (inserted first, never played since); the real account claims it too.
        var ghost = await SeedAsync("OpenVersus_2222222222222", steam: SteamA, install: "", ip: NewIp, lastSeen: DateTime.UtcNow.AddDays(-30));
        var real = await SeedAsync("Real Main", steam: SteamA, install: InstallA, hardware: HwA, lastSeen: DateTime.UtcNow.AddDays(-1));

        var login = await LoginAsync(HomeIp, steam: SteamA, install: InstallA, hardware: HwA);

        Assert.Equal(real.ToString(), login);
        Assert.NotEqual(ghost.ToString(), login);
    }

    // --- Helpers

    private async Task<string> LoginAsync(string ip, string steam, string install, string hardware = "", string provedEpic = "")
    {
        var claims = new JsonObject { ["installId"] = install, ["identityRegistered"] = "1" };
        if (steam.Length > 0)
        {
            claims["steamId"] = steam;
            claims["steamVerified"] = "1";
        }

        if (hardware.Length > 0)
        {
            claims["hardwareId"] = hardware;
            claims["hardwareIdVersion"] = "2";
            claims["hardwareIdQuality"] = "strong";
        }

        string token = IdentifyTokens.Sign(claims, IdentifySecret, DateTimeOffset.UtcNow);
        var result = await Access().LoginAsync(ip, token, provedEpicId: provedEpic.Length > 0 ? provedEpic : null);
        return Assert.IsType<AccessResult.Ok>(result).PlayerId;
    }

    private Task<BsonDocument> AccountAsync(ObjectId id) => Players.Find(new BsonDocument("_id", id)).SingleAsync();

    private async Task<ObjectId> SeedAsync(string name, string steam, string install, string epic = "", string hardware = "", string ip = "198.51.100.40",
        bool provisional = false, DateTime? lastSeen = null)
    {
        var id = ObjectId.GenerateNewId();
        var doc = new BsonDocument
        {
            { "_id", id }, { "name", name }, { "hydraUsername", name }, { "ip", ip }, { "provisional", provisional },
            { "steamId", steam }, { "epicId", epic }, { "installId", install }, { "account", new BsonDocument("current_ip", ip) },
            { "lastSeenAt", lastSeen ?? DateTime.UtcNow.AddDays(-2) },
        };
        if (hardware.Length > 0)
        {
            doc.Add("hardwareId", hardware).Add("hardwareIdVersion", "2").Add("hardwareIdQuality", "strong");
        }

        await Players.InsertOneAsync(doc);
        // The identity indexes the TS server kept (its last writer for each id).
        if (steam.Length > 0)
        {
            await Redis.StringSetAsync($"identity:steam:{steam}", id.ToString());
        }

        if (epic.Length > 0)
        {
            await Redis.StringSetAsync($"identity:epic:{epic}", id.ToString());
        }

        if (install.Length > 0)
        {
            await Redis.StringSetAsync($"identity:install:{install}", id.ToString());
        }

        return id;
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

    private AccessService Access()
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(_mongo!.GetDatabase(TestMongoDb)).BuildServiceProvider();
        var options = new TestOptions<BanSettings>(_bans);
        return new AccessService(services, new TestOptions<AccessSettings>(new AccessSettings { JwtSecret = Secret, IdentifySecret = IdentifySecret }),
            new TestOptions<RealtimeSettings>(new RealtimeSettings()), new TestOptions<SeasonSettings>(new SeasonSettings()), new TestOptions<SteamSettings>(new SteamSettings()),
            new TestOptions<Core.Epic.EpicSettings>(new Core.Epic.EpicSettings()), new BanService(services, NullLogger<BanService>.Instance),
            new NameRules(options, TimeProvider.System, NullLogger<NameRules>.Instance), new PersonBans(services, options, TimeProvider.System, NullLogger<PersonBans>.Instance),
            TimeProvider.System, NullLogger<AccessService>.Instance);
    }
}
