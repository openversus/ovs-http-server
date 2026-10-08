using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Tests;

/// <summary>
/// The website's static files, the name change page and the account picker, against a real Redis (database 12,
/// OVS_TEST_REDIS) and Mongo (a database of its own, dropped: OVS_TEST_MONGO). The terms are invented.
/// </summary>
public sealed class WebPagesTests : IAsyncLifetime
{
    private const int TestRedisDb = 12;
    private const string TestMongoDb = "ovs_web_tests";
    private const string Ip = "198.51.100.60";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-web-").FullName;
    private Factory? _factory;
    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private IMongoCollection<BsonDocument> Players => Mongo.GetCollection<BsonDocument>("playertesters");

    private sealed class Factory(string dir, bool lists) : ServiceFactory<Program>
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
            builder.UseSetting("Bans:BannedNamesFile", Path.Combine(dir, lists ? "banned_names.yaml" : "missing.yaml"));
            builder.UseSetting("Bans:ForceChangeNamesFile", Path.Combine(dir, "force_change_names.yaml"));
            builder.UseSetting("Bans:AllowedNamesFile", Path.Combine(dir, "allowed_names.yaml"));
            builder.UseSetting("Bans:AutoBansFile", Path.Combine(dir, "auto_bans.yaml"));
        }
    }

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "banned_names.yaml"), "terms:\n  - 'zorb'\n");
        File.WriteAllText(Path.Combine(_dir, "force_change_names.yaml"), "terms:\n  - 'blat'\n");
        File.WriteAllText(Path.Combine(_dir, "allowed_names.yaml"), "terms:\n  - 'zorbit'\n");
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
        _factory = new Factory(_dir, lists: true);
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

    private HttpClient Browser(Factory? factory = null, string ip = Ip)
    {
        var client = (factory ?? _factory!).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("x-real-ip", ip);
        return client;
    }

    private async Task<ObjectId> SeedAsync(string name, string ip = Ip, string steam = "")
    {
        var id = ObjectId.GenerateNewId();
        await Players.InsertOneAsync(new BsonDocument { { "_id", id }, { "name", name }, { "ip", ip }, { "steamId", steam }, { "account", new BsonDocument("current_ip", ip) } });
        return id;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostAsync(HttpClient client, string path, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<string> NameOfAsync(ObjectId id) => Players.Find(new BsonDocument("_id", id)).Project(new BsonDocument("name", 1)).SingleAsync().ContinueWith(t => t.Result["name"].AsString);

    [SkippableTheory]
    [InlineData("/theme.css", "text/css")]
    [InlineData("/theme.js", "text/javascript")]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/favicon/favicon-96x96.png", "image/png")]
    [InlineData("/images/OpenVersus_logo.png", "image/png")]
    // One list for both paths, as the TS server's handler.
    [InlineData("/favicon/OpenVersus_logo.png", "image/png")]
    [InlineData("/images/multiversus-bugs-bunny.gif", "image/gif")]
    public async Task ServesTheSitesFiles(string path, string type)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        using var response = await Browser().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [SkippableFact]
    public async Task AnUnknownImageIsNotFoundAndTheUpdateArtIsCached()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        using var missing = await Browser().GetAsync("/images/name_change.html");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Image not found", await missing.Content.ReadAsStringAsync());
        using var art = await Browser().GetAsync("/assets/openversus-update-required-keyart.png");
        Assert.Equal(HttpStatusCode.OK, art.StatusCode);
        Assert.Equal("public, max-age=300", art.Headers.CacheControl?.ToString());
    }

    [SkippableFact]
    public async Task WithNoAccountAtTheIpTheGameMustConnectFirst()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var (status, body) = await GetAsync(Browser(), "/namechange");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("Connect to the game before changing your name.", body);
    }

    [SkippableFact]
    public async Task OneAccountChangesItsName()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Before");
        await Redis.HashSetAsync($"connections:{id}", [new HashEntry("id", id.ToString()), new HashEntry("username", "Before")]);
        await Redis.HashSetAsync($"connections:{Ip}", [new HashEntry("id", id.ToString()), new HashEntry("username", "Before")]);
        var (getStatus, page) = await GetAsync(Browser(), "/namechange");
        Assert.Equal(HttpStatusCode.OK, getStatus);
        Assert.Contains("Before", page);

        var (status, body, _) = await PostAsync(Browser(), "/namechange", new() { ["name"] = "A Brand New Name That Is Too Long  " });

        Assert.Equal(HttpStatusCode.OK, status);
        // Cut to 24 as typed, then trimmed for the record.
        Assert.Equal("A Brand New Name That Is", await NameOfAsync(id));
        Assert.Equal("A Brand New Name That Is", (string?)await Redis.HashGetAsync($"connections:{id}", "username"));
        Assert.Equal("A Brand New Name That Is", (string?)await Redis.HashGetAsync($"connections:{Ip}", "username"));
        Assert.DoesNotContain("not permitted", body);
    }

    [SkippableTheory]
    [InlineData("   ", "Blank or whitespace-only names are not permitted.")]
    [InlineData("the blat", "contains a term which is not permitted in a player name")]
    [InlineData("TAKEN", "is already taken by another player")]
    public async Task ARefusedNameChangesNothing(string name, string message)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Before");
        await SeedAsync("taken", ip: "198.51.100.61");

        var (status, body, _) = await PostAsync(Browser(), "/namechange", new() { ["name"] = name });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(message, body);
        Assert.Equal("Before", await NameOfAsync(id));
        Assert.Equal(0, await Mongo.GetCollection<BsonDocument>("player_bans").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [SkippableFact]
    public async Task ABannedNameBansThePersonWithTheEvidence()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Before", steam: "76561198000000091");
        var client = Browser();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestBrowser/1.0");

        var (status, body, _) = await PostAsync(client, "/namechange", new() { ["name"] = "xx zorb xx and the blat" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("You are now permanently banned", body);
        Assert.DoesNotContain("not permitted in a player name", body);
        Assert.Equal("Before", await NameOfAsync(id));
        var record = await Mongo.GetCollection<BsonDocument>("player_bans").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(("namechange", "xx zorb xx and the blat", "zorb", "TestBrowser/1.0"),
            (record["source"].AsString, record["player"]["attempted_name"].AsString, record["matched"]["term"].AsString, record["request"]["user_agent"].AsString));
        // The request's IP is the account's: it is what found the account.
        Assert.Equal((Ip, Ip), (record["request"]["ip"].AsString, record["identifiers"]["ip"].AsString));
        Assert.True(await Redis.SetContainsAsync("bans:steam", "76561198000000091"));
        Assert.True(await Redis.SetContainsAsync("bans:player", id.ToString()));
    }

    [SkippableFact]
    public async Task AnAllowedWordIsNotABan()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Before");
        await PostAsync(Browser(), "/namechange", new() { ["name"] = "Zorbit Fan" });
        Assert.Equal("Zorbit Fan", await NameOfAsync(id));
    }

    [SkippableFact]
    public async Task WithoutTheListsANameChangeIsRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = await SeedAsync("Before");
        await using var factory = new Factory(_dir, lists: false);
        var (status, body, _) = await PostAsync(Browser(factory), "/namechange", new() { ["name"] = "Anything" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("Name changes are unavailable right now", body);
        Assert.Equal("Before", await NameOfAsync(id));
    }

    private static string VerifyIdIn(string page) => Regex.Match(page, "name=\"verifyId\" value=\"([0-9a-f]{32})\"").Groups[1].Value;

    private static string CookieOf(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ovs_web_account=", StringComparison.Ordinal)).Split(';')[0];

    [SkippableFact]
    public async Task ASharedIpPicksAnAccountThroughACodeInItsGame()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var first = await SeedAsync("First");
        var second = await SeedAsync("Second");
        var (pickStatus, picker) = await GetAsync(Browser(), "/namechange");
        Assert.Equal(HttpStatusCode.OK, pickStatus);
        Assert.Contains("First", picker);
        Assert.Contains("Second", picker);

        // Not in the game: no code.
        var (_, offline, _) = await PostAsync(Browser(), "/account/switch", new() { ["accountId"] = second.ToString(), ["returnTo"] = "/namechange" });
        Assert.Contains("isn't signed in to the game right now", offline);
        // Not at this IP.
        var elsewhere = await SeedAsync("Elsewhere", ip: "198.51.100.62");
        var (_, foreign, _) = await PostAsync(Browser(), "/account/switch", new() { ["accountId"] = elsewhere.ToString(), ["returnTo"] = "/namechange" });
        Assert.Contains("isn't recognized on this network", foreign);

        await Redis.SetAddAsync("online_players", second.ToString());
        var (status, verify, _) = await PostAsync(Browser(), "/account/switch", new() { ["accountId"] = second.ToString(), ["returnTo"] = "/namechange" });
        Assert.Equal(HttpStatusCode.OK, status);
        string verifyId = VerifyIdIn(verify);
        var banner = JsonNode.Parse((string)(await Redis.ListRangeAsync($"dll_notifications:{second}"))[0]!)!.AsObject();
        Assert.Equal(("admin_banner", "Web Login Code", 120), ((string?)banner["type"], (string?)banner["title"], (int?)banner["data"]!["timeout"]));
        string code = Regex.Match((string)banner["message"]!, "Enter ([0-9]{6}) in the browser").Groups[1].Value;

        var (_, wrong, _) = await PostAsync(Browser(), "/account/verify", new() { ["verifyId"] = verifyId, ["code"] = code == "000000" ? "111111" : "000000" });
        Assert.Contains("Wrong code. 4 attempts remaining.", wrong);
        var (foreignStatus, _, _) = await PostAsync(Browser(ip: "198.51.100.63"), "/account/verify", new() { ["verifyId"] = verifyId, ["code"] = code });
        Assert.Equal(HttpStatusCode.Forbidden, foreignStatus);
        // The mismatch spent the code.
        var (spentStatus, _, _) = await PostAsync(Browser(), "/account/verify", new() { ["verifyId"] = verifyId, ["code"] = code });
        Assert.Equal(HttpStatusCode.BadRequest, spentStatus);

        (_, verify, _) = await PostAsync(Browser(), "/account/switch", new() { ["accountId"] = second.ToString(), ["returnTo"] = "/namechange" });
        verifyId = VerifyIdIn(verify);
        banner = JsonNode.Parse((string)(await Redis.ListRangeAsync($"dll_notifications:{second}"))[^1]!)!.AsObject();
        code = Regex.Match((string)banner["message"]!, "Enter ([0-9]{6}) in the browser").Groups[1].Value;
        var (okStatus, _, response) = await PostAsync(Browser(), "/account/verify", new() { ["verifyId"] = verifyId, ["code"] = code });
        Assert.Equal(HttpStatusCode.Redirect, okStatus);
        Assert.Equal("/namechange", response.Headers.Location?.ToString());
        string cookie = CookieOf(response);

        var (pageStatus, page) = await GetAsync(Browser(), "/namechange", cookie);
        Assert.Equal(HttpStatusCode.OK, pageStatus);
        Assert.Contains("Second", page);
        Assert.DoesNotContain("First", page);
        await PostAsync(Browser(), "/namechange", new() { ["name"] = "Renamed" }, cookie);
        Assert.Equal(("First", "Renamed"), (await NameOfAsync(first), await NameOfAsync(second)));

        // An account arrived since the cookie was made: choose again.
        await Redis.StringSetAsync($"admin:ip_changed_at:{Ip}", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        var (_, again) = await GetAsync(Browser(), "/namechange", cookie);
        Assert.Contains("First", again);
        Assert.Contains("Renamed", again);
    }

    [SkippableTheory]
    [InlineData("//elsewhere.example")]
    [InlineData("/\\elsewhere.example")]
    [InlineData("https://elsewhere.example")]
    public async Task AVerifyNeverSendsTheBrowserToAnotherSite(string returnTo)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync("First");
        var second = await SeedAsync("Second");
        await Redis.SetAddAsync("online_players", second.ToString());
        var (_, verify, _) = await PostAsync(Browser(), "/account/switch", new() { ["accountId"] = second.ToString(), ["returnTo"] = returnTo });
        var banner = JsonNode.Parse((string)(await Redis.ListRangeAsync($"dll_notifications:{second}"))[0]!)!.AsObject();
        string code = Regex.Match((string)banner["message"]!, "Enter ([0-9]{6}) in the browser").Groups[1].Value;
        var (status, _, response) = await PostAsync(Browser(), "/account/verify", new() { ["verifyId"] = VerifyIdIn(verify), ["code"] = code });
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal("/home", response.Headers.Location?.ToString());
    }

    [SkippableFact]
    public async Task AnIdentityHeaderAloneReadsNobodysNotifications()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = ObjectId.GenerateNewId().ToString();
        await Redis.HashSetAsync($"connections:{id}", [new HashEntry("id", id)]);
        await Redis.StringSetAsync("identity:steam:76561198000000093", id);
        await Redis.ListRightPushAsync($"dll_notifications:{id}", """{"type":"admin_banner","title":"Web Login Code","message":"Enter 123456 in the browser to verify.","data":{},"timestamp":1}""");
        var client = Browser(ip: "198.51.100.64");
        client.DefaultRequestHeaders.Add("x-steam-id", "76561198000000093");

        Assert.Equal("[]", (await GetAsync(client, "/ovs/notifications")).Body);
        Assert.Equal(1, await Redis.ListLengthAsync($"dll_notifications:{id}"));
    }

    [SkippableFact]
    public async Task TheClientGetsItsNotificationsAndKeepsItsIdentityAlive()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var id = ObjectId.GenerateNewId().ToString();
        await Redis.HashSetAsync($"connections:{id}", [new HashEntry("id", id), new HashEntry("identityRegistered", "")]);
        await Redis.ListRightPushAsync($"dll_notifications:{id}", ["""{"type":"party_invite","title":"t","message":"m","data":{},"timestamp":1}""", """{"type":"toast","title":"t2","message":"m2","data":{"a":1},"timestamp":2}"""]);
        string token = AccessTokens.Sign(new JsonObject
        {
            ["id"] = id, ["steamId"] = "76561198000000092", ["clientVersion"] = "2026.10.01.01", ["identityRegistered"] = "1",
        }, ServiceFactory<Program>.Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        var client = Browser();
        var (_, none) = await GetAsync(client, "/ovs/notifications");
        Assert.Equal("[]", none);
        client.DefaultRequestHeaders.Add("x-hydra-access-token", token);
        var (status, body) = await GetAsync(client, "/ovs/notifications");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["party_invite", "toast"], JsonNode.Parse(body)!.AsArray().Select(n => (string?)n!["type"]));
        Assert.Equal("[]", (await GetAsync(client, "/ovs/notifications")).Body);
        Assert.Equal("76561198000000092", (string?)await Redis.HashGetAsync($"identity:{Ip}", "steamId"));
        Assert.Equal("1", (string?)await Redis.HashGetAsync($"connections:{id}", "identityRegistered"));
        Assert.Equal("2026.10.01.01", (string?)await Redis.HashGetAsync($"connections:{id}", "clientVersion"));
    }
}
