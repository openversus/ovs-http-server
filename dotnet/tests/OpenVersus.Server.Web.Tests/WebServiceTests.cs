using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Web.Tests;

public sealed class WebServiceTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public WebServiceTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.Web.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("GET", "/ovs/notifications")]
    [InlineData("GET", "/home")]
    public async Task ItsRoutesNeedNoSessionToken(string method, string path)
    {
        // They are not game routes: they do their own checks, as in the TS server.
        using var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/home", "GetHome")]
    [InlineData("/leaderboard", "GetLeaderboard")]
    [InlineData("/matches", "GetMatches")]
    public async Task ThePagesRenderWithoutTheStores(string path, string endpoint)
    {
        using var response = await _factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint, response.Headers.GetValues("X-OVS-Endpoint").Single());
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    // The stats page needs the stores (the account at the IP), as /namechange does.
    public async Task TheStatsPageNeedsTheStores()
    {
        using var response = await _factory.CreateClient().GetAsync("/stats");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task TheLiveMatchesSnapshotHasTheTsShape()
    {
        using var response = await _factory.CreateClient().GetAsync("/api/matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(["matches", "count", "onlinePlayers", "searching", "ffaOpen", "generatedAt"], snapshot.Select(p => p.Key));
        Assert.Equal(["1v1", "2v2", "FFA"], snapshot["searching"]!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData("/api/leaderboard/3v3", 400, """{"error":"Invalid mode. Use '1v1' or '2v2'."}""")]
    [InlineData("/api/leaderboard/1v1", 500, """{"error":"Error fetching leaderboard"}""")]
    [InlineData("/api/leaderboard/2v2/me", 200, """{"error":"Not connected to game"}""")]
    [InlineData("/api/leaderboard/3v3/me", 400, """{"error":"Invalid mode. Use '1v1' or '2v2'."}""")]
    public async Task TheLeaderboardApisAnswerAsTheTsRoutesDoWithoutTheStores(string path, int status, string body)
    {
        using var response = await _factory.CreateClient().GetAsync(path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheAdminGateAsksForThePasswordOnceAndKeepsItInACookie()
    {
        var client = _factory.CreateClient();
        using var refused = await client.GetAsync("/admin/banner");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("Admin Login", await refused.Content.ReadAsStringAsync());
        using var refusedApi = await client.GetAsync("/api/admin/banner/online-count");
        Assert.Equal(HttpStatusCode.Unauthorized, refusedApi.StatusCode);
        Assert.Equal("""{"error":"Unauthorized"}""", await refusedApi.Content.ReadAsStringAsync());

        // The default password, as the TS server's; the cookie it sets carries the next request.
        using var login = await client.GetAsync("/admin/banner?pw=changeme");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("admin_auth=")).Split(';')[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/banner") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", cookie);
        using var empty = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("""{"error":"title or message is required"}""", await empty.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAssetSyncNeedsTheToken()
    {
        using var response = await _factory.CreateClient().PostAsync("/syncAsset", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void IsTheWebService()
    {
        Assert.Same(KnownServices.Web, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public void RunsNoBackgroundServiceOfItsOwn()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true).Select(t => t.Name).Order().ToList();

        Assert.Equal(["BannedPlayers", "ClusterSettingsSync", "InstanceHeartbeat"], hosted);
    }

    [Fact]
    public void ItsReadinessDependsOnlyOnStores()
    {
        var ready = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(r => r.Tags.Contains("ready")).Select(r => r.Name).ToList();

        Assert.NotEmpty(ready);
        Assert.All(ready, name => Assert.Contains(name, new[] { "redis", "mongo" }));
    }

    [Fact]
    public async Task IsAliveButNotReadyWithoutItsStores()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public void EverySettingItsServicesReadIsBound()
    {
        var problems = Registrations.UnboundOptions(_factory.Registered, _factory.Services);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryServiceItsEndpointsLookUpIsRegistered()
    {
        var problems = Registrations.UnresolvableLookups(_factory.Services, typeof(Program).Assembly, _factory.Registered);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
