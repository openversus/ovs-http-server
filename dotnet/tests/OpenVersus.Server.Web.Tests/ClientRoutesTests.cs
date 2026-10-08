using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Web.Tests;

/// <summary>
/// GET /ovs/client-version with a release of this test's own in GitHub's place, and GET /ovs/all-players against a real
/// Mongo (a database of its own, dropped: OVS_TEST_MONGO; Redis database 10, OVS_TEST_REDIS, for the host).
/// </summary>
public sealed class ClientRoutesTests : IAsyncLifetime
{
    private const int TestRedisDb = 10;
    private const string TestMongoDb = "ovs_client_routes_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private IMongoClient? _mongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private sealed class FakeReleases(Func<JsonObject> latest) : IClientReleases
    {
        public int Calls { get; private set; }

        public string Repo => "openversus/ovs-client";

        public Task<JsonObject> LatestAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(latest());
        }
    }

    private sealed class Factory(IClientReleases releases, bool stores, string minimumVersion = "", bool versionCheck = true) : ServiceFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (stores)
            {
                string[] parts = s_redis!.Split(':');
                builder.UseSetting("REDIS", parts[0]);
                builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
                builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
                builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
                builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
                builder.UseSetting("MONGODB_URI", new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl().ToString());
            }

            builder.UseSetting("Control:Port", "0");
            builder.UseSetting("Control:Socket", "off");
            builder.UseSetting("Clients:MinimumVersion", minimumVersion);
            builder.UseSetting("Clients:VersionCheck", versionCheck ? "true" : "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClientReleases>();
                services.AddSingleton(releases);
            });
        }
    }

    private static JsonObject Release(string tag, string name, params (string Name, long Size)[] assets) => new()
    {
        ["tag_name"] = tag,
        ["name"] = name,
        ["assets"] = new JsonArray([.. assets.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.Name,
            ["size"] = a.Size,
            ["digest"] = "sha256:" + new string('b', 64),
            ["browser_download_url"] = $"https://github.com/openversus/ovs-client/releases/download/{tag}/{a.Name}",
        })]),
    };

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        _mongo = new MongoClient(new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb }.ToMongoUrl());
        await _mongo.DropDatabaseAsync(TestMongoDb);
    }

    public async Task DisposeAsync()
    {
        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    [Fact]
    public async Task OffersTheLatestReleaseWithItsFilesFlatAndListed()
    {
        var releases = new FakeReleases(() => Release("v2026.10.08.1", "2026.10.08.1 release",
            ("OpenVersus_2026.10.08.1.asi", 216576), ("OpenVersus_2026.10.08.1.asi.sha256", 90), ("OpenVersus_v2026.10.08.1.zip", 469126), ("OVS_P.pak", 10), ("OVS_P.utoc", 20), ("OVS_P.ucas", 30)));
        await using var factory = new Factory(releases, stores: false, minimumVersion: "2026.10.01.1");

        var (status, body) = await GetAsync(factory, "/ovs/client-version?v=2026.09.30.5");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("2026.10.08.1", "https://github.com/openversus/ovs-client/releases/download/v2026.10.08.1/OpenVersus_2026.10.08.1.asi", false, "2026.10.01.1", true, "2026.10.08.1 release"),
            ((string?)body["latest_version"], (string?)body["download_url"], (bool?)body["is_latest"], (string?)body["minimum_version"], (bool?)body["update_required"], (string?)body["release_name"]));
        Assert.Equal(["OVS_P.pak", "OVS_P.ucas", "OVS_P.utoc", "OpenVersus_2026.10.08.1.asi"], body["files"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Equal(("paks", 10L, new string('b', 64)), ((string?)body["files"]![0]!["kind"], (long?)body["files"]![0]!["size"], (string?)body["files"]![0]!["sha256"]));
        Assert.Equal((4, "OpenVersus_2026.10.08.1.asi", "plugin", 216576L), ((int?)body["file_count"], (string?)body["file_3_name"], (string?)body["file_3_kind"], (long?)body["file_3_size"]));
        Assert.Equal(["latest_version", "download_url", "is_latest", "minimum_version", "update_required", "release_name", "files", "file_count"], body.AsObject().Select(p => p.Key).Take(8));

        // A current client, and one newer than the latest release (a test build), are "latest" and owe no update.
        var (_, current) = await GetAsync(factory, "/ovs/client-version?v=2026.10.08.1");
        Assert.Equal((true, false), ((bool?)current["is_latest"], (bool?)current["update_required"]));
        var (_, newer) = await GetAsync(factory, "/ovs/client-version?v=2026.10.09.1");
        Assert.True((bool?)newer["is_latest"]);
        // A client that states no version is never latest and, with a minimum set, must update.
        var (_, legacy) = await GetAsync(factory, "/ovs/client-version");
        Assert.Equal((false, true), ((bool?)legacy["is_latest"], (bool?)legacy["update_required"]));
    }

    [Fact]
    public async Task AReleaseThatCannotBeOfferedOrNoAnswerGivesTheFallback()
    {
        var broken = new FakeReleases(() => Release("2026.10.08.1", "no plugin", ("OpenVersus_v2026.10.08.1.zip", 1)));
        await using var factory = new Factory(broken, stores: false);
        var (status, body) = await GetAsync(factory, "/ovs/client-version?v=2026.10.08.1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("", "", true, ""), ((string?)body["latest_version"], (string?)body["download_url"], (bool?)body["is_latest"], (string?)body["release_name"]));
        Assert.False(body.AsObject().ContainsKey("files"));

        var down = new FakeReleases(() => throw new ClientReleaseException("GitHub API returned 503"));
        await using var offline = new Factory(down, stores: false);
        (_, body) = await GetAsync(offline, "/ovs/client-version?v=2026.10.08.1");
        Assert.Equal(("", true), ((string?)body["latest_version"], (bool?)body["is_latest"]));

        // A timeout surfaces as a cancellation that is not the request's own: still the fallback, never a 500.
        var slow = new FakeReleases(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));
        await using var timedOut = new Factory(slow, stores: false);
        (status, body) = await GetAsync(timedOut, "/ovs/client-version?v=2026.10.08.1");
        Assert.Equal((HttpStatusCode.OK, "", true), (status, (string?)body["latest_version"], (bool?)body["is_latest"]));
    }

    [Fact]
    public async Task WithoutAMinimumNoClientMustUpdate()
    {
        var releases = new FakeReleases(() => Release("2026.10.08.1", "r", ("OpenVersus.asi", 5)));
        await using var factory = new Factory(releases, stores: false);

        var (_, old) = await GetAsync(factory, "/ovs/client-version?v=2020.01.01.1");
        var (_, none) = await GetAsync(factory, "/ovs/client-version");

        Assert.Equal((false, false, ""), ((bool?)old["is_latest"], (bool?)old["update_required"], (string?)old["minimum_version"]));
        Assert.Equal((false, false), ((bool?)none["is_latest"], (bool?)none["update_required"]));
    }

    [Fact]
    public async Task WithTheVersionCheckOffNoUpdateIsRequired()
    {
        var releases = new FakeReleases(() => Release("2026.10.08.1", "r", ("OpenVersus.asi", 5)));
        await using var factory = new Factory(releases, stores: false, minimumVersion: "2026.10.01.1", versionCheck: false);

        var (_, body) = await GetAsync(factory, "/ovs/client-version?v=2020.01.01.1");

        Assert.Equal((false, false, "2026.10.01.1"), ((bool?)body["is_latest"], (bool?)body["update_required"], (string?)body["minimum_version"]));
    }

    [SkippableFact]
    public async Task ListsTheFirstAccountsWithANameAsIdAndUsername()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var players = _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters");
        var named = ObjectId.GenerateNewId();
        await players.InsertManyAsync([
            new BsonDocument { { "_id", named }, { "name", "Named One" }, { "ip", "198.51.100.1" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "" }, { "ip", "198.51.100.2" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ip", "198.51.100.3" } },
        ]);
        await using var factory = new Factory(new FakeReleases(() => []), stores: true);

        var (status, body) = await GetAsync(factory, "/ovs/all-players");

        Assert.Equal(HttpStatusCode.OK, status);
        var list = body.AsArray();
        Assert.Single(list);
        Assert.Equal((named.ToString(), "Named One"), ((string?)list[0]!["accountId"], (string?)list[0]!["username"]));
    }

    [Fact]
    public async Task WithoutAStoreAllPlayersIsAnEmptyList()
    {
        await using var factory = new Factory(new FakeReleases(() => []), stores: false);

        var (status, body) = await GetAsync(factory, "/ovs/all-players");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.AsArray());
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> GetAsync(Factory factory, string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }
}
