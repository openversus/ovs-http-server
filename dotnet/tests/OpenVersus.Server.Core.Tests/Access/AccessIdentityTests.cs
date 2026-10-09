using System.Security.Cryptography;
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
using OpenVersus.Server.Identity;
using OpenVersus.Server.Identity.Steam;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>
/// The login's identity when the client's Steam id must be proved: an identify token on the login (the game has no
/// session token yet) or the identity record counts a Steam id only with a ticket's proof, and the proved ticket's
/// fields reach the account. Against a real Redis (database 15, OVS_TEST_REDIS) and Mongo (a database of its own,
/// dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class AccessIdentityTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_access_identity_tests";
    private const string Secret = "access-identity-game-secret-0123456789abcdef";
    private const string IdentifySecret = "access-identity-identify-secret-0123456789";
    private const string Ip = "198.51.100.70";
    private const string Install = "0123456789abcdef0123456789abcdef", OtherInstall = "fedcba9876543210fedcba9876543210";
    private static readonly string s_steam = SteamTickets.SteamId.ToString();
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-access-identity-").FullName;
    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private BanSettings _settings = new();
    private readonly SteamSettings _steam = new();
    private readonly Core.Epic.EpicSettings _epic = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _redis!.GetDatabase();

    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private IMongoCollection<BsonDocument> Players => Mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);

    public async Task InitializeAsync()
    {
        _settings = new BanSettings
        {
            IpFile = null, CidrFile = null, EpicIdFile = null, HardwareFile = null, InstallIdFile = null, SteamIdFile = null, AllowedNamesFile = null,
            AutoBansFile = Path.Combine(_dir, "auto_bans.yaml"),
            BannedNamesFile = Path.Combine(_dir, "banned_names.yaml"),
            ForceChangeNamesFile = Path.Combine(_dir, "force_change_names.yaml"),
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

    [SkippableFact]
    public async Task AnIdentifyTokenLogsInByItsProvedSteamIdAndTheTicketReachesTheAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Proved", steam: s_steam, install: OtherInstall);
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("steamVerified", "1"), new HashEntry("steamTicket", TicketJson()), new HashEntry("installId", Install)]);

        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: true)));

        Assert.Equal(account.ToString(), result.PlayerId);
        var saved = await Players.Find(new BsonDocument("_id", account)).SingleAsync();
        var ticket = saved["steamTicket"].AsBsonDocument;
        Assert.Equal(s_steam, ticket["steam_id"].AsString);
        Assert.Equal(64, ticket["ticket_hash"].AsString.Length);
        Assert.Equal(1818750, ticket["app_id"].AsInt64);
        Assert.Equal(Install, saved["installId"].AsString);
        Assert.Equal(account.ToString(), (string?)await Redis.StringGetAsync($"identity:steam:{s_steam}"));
    }

    [SkippableFact]
    public async Task ASteamIdSteamRefusedLatelyIsAClaim()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        _steam.Enabled = true;
        var steamOwner = await SeedAsync("Steam Owner", steam: s_steam, install: "");
        var installOwner = await SeedAsync("Install Owner", steam: "", install: Install);
        // The Steam identity service heard Steam refuse this id's ticket a minute ago.
        await Redis.HashSetAsync(SteamSessions.SessionKey(s_steam), [new HashEntry("state", SteamSessions.Refused), new HashEntry("response", "NoLicenseOrExpired"),
            new HashEntry("verdict_at", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds().ToString())]);

        var held = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: true)));
        Assert.Equal(installOwner.ToString(), held.PlayerId);

        // The hold has passed.
        await Redis.HashSetAsync(SteamSessions.SessionKey(s_steam), "verdict_at", DateTimeOffset.UtcNow.AddMinutes(-11).ToUnixTimeMilliseconds().ToString());
        var past = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: true)));
        Assert.Equal(steamOwner.ToString(), past.PlayerId);
    }

    [SkippableFact]
    public async Task AnIdentifyTokenWithoutProofLogsInByTheInstallId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var steamOwner = await SeedAsync("Steam Owner", steam: s_steam, install: "");
        var installOwner = await SeedAsync("Install Owner", steam: "", install: Install);

        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: false)));

        Assert.Equal(installOwner.ToString(), result.PlayerId);
        var untouched = await Players.Find(new BsonDocument("_id", steamOwner)).SingleAsync();
        Assert.False(untouched.Contains("steamTicket"));
        // The claimed id was not written onto the install's account either.
        Assert.Equal("", (await Players.Find(new BsonDocument("_id", installOwner)).SingleAsync())["steamId"].AsString);
    }

    [SkippableFact]
    public async Task AnUnprovedSteamIdInTheIdentityRecordIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync("Steam Owner", steam: s_steam, install: "");
        var installOwner = await SeedAsync("Install Owner", steam: "", install: Install);
        // A record from before tickets (no steamVerified), as the TS server wrote it.
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("installId", Install), new HashEntry("identityRegistered", "1")]);

        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, null));

        Assert.Equal(installOwner.ToString(), result.PlayerId);
    }

    [SkippableFact]
    public async Task AFirstLaunchCarriesTheTicketToTheNewAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("steamVerified", "1"), new HashEntry("steamTicket", TicketJson()), new HashEntry("installId", Install), new HashEntry("identityRegistered", "1")]);

        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, null));

        var created = await Players.Find(new BsonDocument("_id", ObjectId.Parse(result.PlayerId))).SingleAsync();
        Assert.Equal(s_steam, created["steamId"].AsString);
        Assert.Equal(s_steam, created["steamTicket"]["steam_id"].AsString);
        Assert.False(created["provisional"].AsBoolean);
    }

    private const string Epic = "0123456789abcdef0123456789abcde0";

    [SkippableFact]
    public async Task WithoutAnEpicClientIdAClaimedEpicIdStillLogsIn()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var epicOwner = await SeedAsync("Epic Owner", steam: "", install: "", epic: Epic);
        await SeedAsync("Install Owner", steam: "", install: Install);
        // The identify token (no verification configured anywhere) and the record both name the id as a claim.
        var byToken = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, EpicIdentifyToken(verified: false)));
        Assert.Equal(epicOwner.ToString(), byToken.PlayerId);
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("epicId", Epic), new HashEntry("installId", Install)]);
        var byRecord = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, null));
        Assert.Equal(epicOwner.ToString(), byRecord.PlayerId);
    }

    [SkippableFact]
    public async Task WithAnEpicClientIdOnlyAProvedEpicIdLogsIn()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        _epic.ClientId = "client";
        var epicOwner = await SeedAsync("Epic Owner", steam: "", install: "", epic: Epic);
        var installOwner = await SeedAsync("Install Owner", steam: "", install: Install);
        // Unproved first: proved, the login would attach the install to the Epic owner's account, as it should.
        var claimed = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, EpicIdentifyToken(verified: false)));
        Assert.Equal(installOwner.ToString(), claimed.PlayerId);
        var proved = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, EpicIdentifyToken(verified: true)));
        Assert.Equal(epicOwner.ToString(), proved.PlayerId);
    }

    [SkippableFact]
    public async Task WithAnEpicClientIdAnUnprovedEpicIdInTheIdentityRecordIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        _epic.ClientId = "client";
        var epicOwner = await SeedAsync("Epic Owner", steam: "", install: "", epic: Epic);
        var installOwner = await SeedAsync("Install Owner", steam: "", install: Install);
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("epicId", Epic), new HashEntry("installId", Install)]);
        var claimed = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, null));
        Assert.Equal(installOwner.ToString(), claimed.PlayerId);
        await Redis.HashSetAsync($"identity:{Ip}", "epicVerified", "1");
        var proved = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, null));
        Assert.Equal(epicOwner.ToString(), proved.PlayerId);
    }

    [SkippableFact]
    public async Task AProvedSteamIdReplacesOneTheInstallsAccountHeldAsAClaim()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        const string claimed = "76561198000000002";
        var account = await SeedAsync("Claimed", steam: claimed, install: Install);
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("steamVerified", "1"), new HashEntry("steamTicket", TicketJson()), new HashEntry("installId", Install)]);
        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: true)));
        Assert.Equal(account.ToString(), result.PlayerId);
        var saved = await Players.Find(new BsonDocument("_id", account)).SingleAsync();
        Assert.Equal(s_steam, saved["steamId"].AsString);
        Assert.Equal(s_steam, saved["steamTicket"]["steam_id"].AsString);
    }

    [SkippableFact]
    public async Task AProvedSteamIdDoesNotDisplaceAnotherProvedOne()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        const string sibling = "76561198000000002";
        var account = await SeedAsync("Sibling", steam: sibling, install: Install);
        await Players.UpdateOneAsync(new BsonDocument("_id", account), new BsonDocument("$set", new BsonDocument("steamTicket", new BsonDocument { { "steam_id", sibling }, { "ticket_hash", "x" } })));
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("steamVerified", "1"), new HashEntry("steamTicket", TicketJson()), new HashEntry("installId", Install)]);
        // Two proved Steam accounts on one install are two people: the sibling keeps their account, this login gets its own.
        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, IdentifyToken(s_steam, verified: true)));
        Assert.NotEqual(account.ToString(), result.PlayerId);
        var saved = await Players.Find(new BsonDocument("_id", account)).SingleAsync();
        Assert.Equal(sibling, saved["steamId"].AsString);
        Assert.Equal(sibling, saved["steamTicket"]["steam_id"].AsString);
        var own = await Players.Find(new BsonDocument("_id", ObjectId.Parse(result.PlayerId))).SingleAsync();
        Assert.Equal(s_steam, own["steamId"].AsString);
    }

    [SkippableFact]
    public async Task AProvedEpicIdReplacesOneTheInstallsAccountHeldAsAClaimAndIsMarkedProved()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        _epic.ClientId = "client";
        var account = await SeedAsync("Claimed", steam: "", install: Install, epic: "ffffffffffffffffffffffffffffffff");
        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, EpicIdentifyToken(verified: true)));
        Assert.Equal(account.ToString(), result.PlayerId);
        var saved = await Players.Find(new BsonDocument("_id", account)).SingleAsync();
        Assert.Equal(Epic, saved["epicId"].AsString);
        Assert.True(saved[IdentityRecord.EpicProvedField].IsValidDateTime);

        // Proved now: a different proved id reaching the install is another person, with an account of their own.
        await Redis.KeyDeleteAsync($"identity:epic:{Epic}");
        var other = IdentifyTokens.Sign(new JsonObject { ["epicId"] = "0123456789abcdef0123456789abcde1", ["installId"] = Install, ["identityRegistered"] = "1", ["epicVerified"] = "1" }, IdentifySecret, DateTimeOffset.UtcNow);
        var theirs = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, other));
        Assert.NotEqual(account.ToString(), theirs.PlayerId);
        Assert.Equal(Epic, (await Players.Find(new BsonDocument("_id", account)).SingleAsync())["epicId"].AsString);
    }

    private static string EpicIdentifyToken(bool verified) =>
        IdentifyTokens.Sign(new JsonObject { ["epicId"] = Epic, ["installId"] = Install, ["identityRegistered"] = "1", ["epicVerified"] = verified ? "1" : "" }, IdentifySecret, DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task TheGamesOwnSessionTokenStillBindsByTheAccountsSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync("Returning", steam: s_steam, install: Install);
        string game = AccessTokens.Sign(new JsonObject { ["id"] = account.ToString(), ["steamId"] = s_steam, ["installId"] = Install, ["identityRegistered"] = "1" }, Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        var result = Assert.IsType<AccessResult.Ok>(await Access().LoginAsync(Ip, game));

        Assert.Equal(account.ToString(), result.PlayerId);
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

    private IServiceProvider Stores() => new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(Mongo).BuildServiceProvider();

    private AccessService Access()
    {
        var options = new TestOptions<BanSettings>(_settings);
        var services = Stores();
        return new AccessService(services, new TestOptions<AccessSettings>(new AccessSettings { JwtSecret = Secret, IdentifySecret = IdentifySecret }),
            new TestOptions<RealtimeSettings>(new RealtimeSettings()), new TestOptions<SeasonSettings>(new SeasonSettings()), new TestOptions<SteamSettings>(_steam),
            new TestOptions<Core.Epic.EpicSettings>(_epic), new BanService(services, NullLogger<BanService>.Instance), new NameRules(options, TimeProvider.System, NullLogger<NameRules>.Instance),
            new PersonBans(services, options, TimeProvider.System, NullLogger<PersonBans>.Instance), TimeProvider.System, NullLogger<AccessService>.Instance);
    }

    // The token /api/identify signed for the client, which the game's login carries when the game has none of its own.
    private static string IdentifyToken(string steam, bool verified) =>
        IdentifyTokens.Sign(new JsonObject { ["steamId"] = steam, ["installId"] = Install, ["identityRegistered"] = "1", ["steamVerified"] = verified ? "1" : "" }, IdentifySecret, DateTimeOffset.UtcNow);

    // A verified ticket's fields as /api/identify leaves them in identity:{ip}.
    private static string TicketJson()
    {
        using var key = SteamTickets.NewKey();
        using var verifier = new SteamTicketVerifier(SteamTickets.PublicOf(key));
        var verified = Assert.IsType<SteamTicketCheck.Verified>(verifier.Check(SteamTickets.Session(key), SteamTickets.AppId, SteamTickets.Now));
        return IdentifyService.TicketFields(verified, Ip, SteamTickets.Now).ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson });
    }

    private async Task<ObjectId> SeedAsync(string name, string steam, string install, string epic = "")
    {
        var id = ObjectId.GenerateNewId();
        await Players.InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "name", name }, { "hydraUsername", "OpenVersus_1234567890123" }, { "ip", "198.51.100.40" },
            { "steamId", steam }, { "epicId", epic }, { "installId", install }, { "account", new BsonDocument("current_ip", "198.51.100.41") },
        });
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
}
