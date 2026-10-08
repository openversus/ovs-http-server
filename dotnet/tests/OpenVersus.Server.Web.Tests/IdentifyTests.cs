using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Identity;
using OpenVersus.Server.Identity.Steam;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Tests;

/// <summary>
/// POST /api/identify against a real Redis (database 11, OVS_TEST_REDIS) and Mongo (a database of its own, dropped:
/// OVS_TEST_MONGO), with a ticket verifier that trusts this test's key in Steam's place.
/// </summary>
public sealed class IdentifyTests : IAsyncLifetime
{
    private const int TestRedisDb = 11;
    private const string TestMongoDb = "ovs_identify_tests";
    private const string Ip = "198.51.100.80";
    private const string Install = "0123456789abcdef0123456789abcdef";
    private static readonly string s_steam = SteamTickets.SteamId.ToString();
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private static readonly RSA s_key = SteamTickets.NewKey();
    private Factory? _factory;
    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

    private IMongoCollection<BsonDocument> Players => _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");

    private sealed class Factory(string minimumVersion = "") : ServiceFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            string[] parts = s_redis!.Split(':');
            builder.UseSetting("REDIS", parts[0]);
            builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
            builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
            builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
            builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
            builder.UseSetting("MONGODB_URI", new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl().ToString());
            builder.UseSetting("Control:Port", "0");
            builder.UseSetting("Control:Socket", "off");
            builder.UseSetting("Clients:MinimumVersion", minimumVersion);
            // Steam's key cannot sign a test's ticket: the verifier trusts this test's key instead.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamTicketVerifier>();
                services.AddSingleton<ISteamTicketVerifier>(new SteamTicketVerifier(SteamTickets.PublicOf(s_key)));
            });
        }
    }

    public async Task InitializeAsync()
    {
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
        });
        _mongo = new MongoClient(new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl());
        await _mongo.DropDatabaseAsync(TestMongoDb);
        await ClearRedisAsync();
        _factory = new Factory();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_redis is not null)
        {
            await ClearRedisAsync();
            _redis.Dispose();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    [SkippableFact]
    public async Task AClientWithNoIdentifierMustUpdateButIsStillRecorded()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");

        var (status, body) = await IdentifyAsync(new JsonObject());

        Assert.Equal(HttpStatusCode.UpgradeRequired, status);
        Assert.Equal((false, "client_update_required", true, ""), ((bool?)body["ok"], (string?)body["error"], (bool?)body["identityRequired"], (string?)body["minimumVersion"]));
        Assert.Equal("", (string?)await Redis.HashGetAsync($"identity:{Ip}", "identityRegistered"));
    }

    [SkippableFact]
    public async Task RegistersAnInstallAndSignsTheIdentifyToken()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var request = new JsonObject
        {
            ["installId"] = Install.ToUpperInvariant(), ["clientVersion"] = " 2026.10.08.1 ", ["nodePort"] = 7777,
            ["hardwareId"] = new string('a', 64), ["hardwareIdVersion"] = 2, ["hardwareIdQuality"] = "Strong",
        };

        var (status, body) = await IdentifyAsync(request);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool?)body["ok"]);
        Assert.Null(body["accountId"]?.GetValue<string?>());
        string token = (string)body["token"]!;
        var claims = IdentifyTokens.Verify(token, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow);
        Assert.NotNull(claims);
        Assert.Equal(["id", "steamId", "epicId", "hardwareId", "hardwareIdVersion", "hardwareIdQuality", "installId", "clientVersion", "identityRegistered", "nodePort", "current_ip", "profile_id", "public_id", "wb_network_id", "username", "hydraUsername", "lobby_id", "GameplayPreferences", "steamVerified", "iat", "exp"],
            claims.Select(c => c.Key));
        Assert.Equal((Install, "2026.10.08.1", "1", "7777", Ip, "", new string('a', 64), "2"), ((string?)claims["installId"], (string?)claims["clientVersion"], (string?)claims["identityRegistered"], (string?)claims["nodePort"], (string?)claims["current_ip"], (string?)claims["steamVerified"], (string?)claims["hardwareId"], (string?)claims["hardwareIdVersion"]));
        Assert.Equal(964, (int?)claims["GameplayPreferences"]);
        // Not a game session token (HttpServiceTests checks a game route refuses it).
        Assert.Throws<AccessTokenException>(() => AccessTokens.Verify(token, ServiceFactory<Program>.Secret, DateTimeOffset.UtcNow));
        var record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal((Install, "1", "7777", "", "", "strong"), (record["installId"], record["identityRegistered"], record["nodePort"], record["steamId"], record["steamVerified"], record["hardwareIdQuality"]));
        Assert.InRange((await Redis.KeyTimeToLiveAsync($"identity:{Ip}"))!.Value, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300));

    }

    [SkippableFact]
    public async Task AClaimedSteamIdWithoutATicketIsNoIdentity()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync(s_steam);

        var (status, body) = await IdentifyAsync(new JsonObject { ["steamId"] = s_steam, ["installId"] = Install, ["clientVersion"] = "2026.10.08.1" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["accountId"]?.GetValue<string?>());
        var claims = IdentifyTokens.Verify((string)body["token"]!, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow)!;
        Assert.Equal(("", "", ""), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["id"]));
        Assert.Equal("", (string?)await Redis.HashGetAsync($"identity:{Ip}", "steamId"));
        Assert.False((await Players.Find(new BsonDocument("_id", account)).SingleAsync()).Contains("steamTicket"));
    }

    [SkippableFact]
    public async Task AVerifiedTicketIsTheSteamIdAndItsFieldsReachTheAccount()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var account = await SeedAsync(s_steam);
        await Redis.HashSetAsync($"connections:{account}", [new HashEntry("id", account.ToString()), new HashEntry("identityRegistered", "")]);
        byte[] ticket = SteamTickets.Session(s_key, licenses: [735341]);
        var request = new JsonObject { ["steamId"] = "76561198000000001", ["steamTicket"] = Convert.ToHexString(ticket), ["installId"] = Install, ["clientVersion"] = "2026.10.08.1", ["nodePort"] = "7001" };

        var (status, body) = await IdentifyAsync(request);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(account.ToString(), (string?)body["accountId"]);
        var claims = IdentifyTokens.Verify((string)body["token"]!, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow)!;
        Assert.Equal((s_steam, "1", account.ToString(), account.ToString()), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["id"], (string?)claims["wb_network_id"]));
        var record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal((s_steam, "1"), (record["steamId"], record["steamVerified"]));
        Assert.Contains("ticket_hash", record["steamTicket"]);
        var saved = await Players.Find(new BsonDocument("_id", account)).SingleAsync();
        var stored = saved["steamTicket"].AsBsonDocument;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(ticket)), stored["ticket_hash"].AsString);
        Assert.Equal((s_steam, 1818750L, 2L), (stored["steam_id"].AsString, stored["app_id"].AsInt64, stored["version"].AsInt64));
        Assert.Equal([735341L], stored["licenses"].AsBsonArray.Select(l => l.AsInt64));
        Assert.Equal(("203.0.113.7", "192.168.1.20", "203.0.113.7", 90000L, 3L, Ip), (stored["ownership_external_ip"].AsString, stored["ownership_internal_ip"].AsString, stored["session_external_ip"].AsString, stored["connection_time"].AsInt64, stored["connection_count"].AsInt64, stored["request_ip"].AsString));
        Assert.Equal(SteamTickets.Expires.UtcDateTime, stored["ownership_expires_at"].ToUniversalTime());
        Assert.True(stored["received_at"].IsValidDateTime);
        // Never the ticket itself.
        Assert.DoesNotContain(Convert.ToHexStringLower(ticket)[..32], saved.ToJson().ToLowerInvariant());
        // The live session is unlocked, with the node port.
        Assert.Equal(("1", "2026.10.08.1", "7001"), ((string?)await Redis.HashGetAsync($"connections:{account}", "identityRegistered"), (string?)await Redis.HashGetAsync($"connections:{account}", "clientVersion"), (string?)await Redis.HashGetAsync($"connections:{account}", "nodePort")));
    }

    public static TheoryData<string, byte[]> RefusedTickets() => new()
    {
        { "another app", SteamTickets.Session(s_key, appId: 480) },
        { "unsigned", SteamTickets.Full(SteamTickets.Ownership()) },
        { "expired", SteamTickets.Session(s_key, expires: DateTimeOffset.UtcNow.AddDays(-1)) },
        { "not a ticket", Encoding.ASCII.GetBytes("this is not a ticket at all, not even close to one") },
    };

    [SkippableTheory]
    [MemberData(nameof(RefusedTickets))]
    public async Task ARefusedTicketLeavesNoSteamId(string name, byte[] ticket)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(s_steam);

        var (status, body) = await IdentifyAsync(new JsonObject { ["steamId"] = s_steam, ["steamTicket"] = Convert.ToHexString(ticket), ["installId"] = Install, ["clientVersion"] = "2026.10.08.1" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["accountId"]?.GetValue<string?>());
        Assert.Equal(("", ""), ((string?)await Redis.HashGetAsync($"identity:{Ip}", "steamId"), (string?)await Redis.HashGetAsync($"identity:{Ip}", "steamVerified")));
        Assert.NotEmpty(name);
    }

    [SkippableFact]
    public async Task ASecondRegistrationFromTheSameInstallKeepsTheProvedSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await IdentifyAsync(new JsonObject { ["steamTicket"] = Convert.ToHexString(SteamTickets.Session(s_key)), ["installId"] = Install, ["clientVersion"] = "2026.10.08.1" });

        // The same install, a moment later, without the ticket (the TS client sent the Steam id alone on its retries).
        var (_, body) = await IdentifyAsync(new JsonObject { ["steamId"] = s_steam, ["installId"] = Install, ["clientVersion"] = "2026.10.08.1", ["nodePort"] = 7002 });
        var claims = IdentifyTokens.Verify((string)body["token"]!, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow)!;
        var record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal((s_steam, "1", "7002"), (record["steamId"], record["steamVerified"], record["nodePort"]));
        Assert.Contains("ticket_hash", record["steamTicket"]);
        Assert.Equal((s_steam, "1"), ((string?)claims["steamId"], (string?)claims["steamVerified"]));

        // Another install behind the IP replaces the record.
        await IdentifyAsync(new JsonObject { ["steamId"] = s_steam, ["installId"] = "fedcba9876543210fedcba9876543210", ["clientVersion"] = "2026.10.08.1" });
        record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal(("", "", ""), (record["steamId"], record["steamVerified"], record["steamTicket"]));
    }

    [SkippableFact]
    public async Task ARecordFromBeforeTicketsLendsNoSteamIdToTheSameInstall()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(s_steam);
        // What the TS server wrote for this install minutes before the cutover: a claimed Steam id, no proof.
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("installId", Install), new HashEntry("identityRegistered", "1"), new HashEntry("clientVersion", "2026.10.07.1")]);

        var (_, body) = await IdentifyAsync(new JsonObject { ["installId"] = Install, ["clientVersion"] = "2026.10.08.1" });

        Assert.Null(body["accountId"]?.GetValue<string?>());
        var claims = IdentifyTokens.Verify((string)body["token"]!, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow)!;
        Assert.Equal(("", ""), ((string?)claims["steamId"], (string?)claims["steamVerified"]));
        Assert.Equal(("", ""), ((string?)await Redis.HashGetAsync($"identity:{Ip}", "steamId"), (string?)await Redis.HashGetAsync($"identity:{Ip}", "steamVerified")));
    }

    [SkippableFact]
    public async Task AnOutdatedClientMustUpdate()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await using var factory = new Factory(minimumVersion: "2026.10.01.1");

        var (status, body) = await IdentifyAsync(new JsonObject { ["installId"] = Install, ["clientVersion"] = "2026.09.30.5" }, factory);

        Assert.Equal(HttpStatusCode.UpgradeRequired, status);
        Assert.Equal((false, "client_update_required", false, "2026.10.01.1"), ((bool?)body["ok"], (string?)body["error"], (bool?)body["identityRequired"], (string?)body["minimumVersion"]));
        var (okStatus, _) = await IdentifyAsync(new JsonObject { ["installId"] = Install, ["clientVersion"] = "2026.10.01.1" }, factory);
        Assert.Equal(HttpStatusCode.OK, okStatus);
    }

    [SkippableFact]
    public async Task MalformedJsonIsRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");

        var response = await Client().PostAsync("/api/identify", new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid JSON", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]);
    }

    private HttpClient Client(Factory? factory = null)
    {
        var client = (factory ?? _factory!).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("x-real-ip", Ip);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> IdentifyAsync(JsonObject request, Factory? factory = null)
    {
        var response = await Client(factory).PostAsync("/api/identify", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private async Task<ObjectId> SeedAsync(string steam)
    {
        var id = ObjectId.GenerateNewId();
        await Players.InsertOneAsync(new BsonDocument { { "_id", id }, { "name", "Seeded" }, { "ip", "198.51.100.40" }, { "steamId", steam }, { "installId", "" }, { "account", new BsonDocument("current_ip", "198.51.100.41") } });
        await Redis.StringSetAsync($"identity:steam:{steam}", id.ToString());
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
}
