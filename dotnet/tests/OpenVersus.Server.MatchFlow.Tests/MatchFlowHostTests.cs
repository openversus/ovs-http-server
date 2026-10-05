using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.MatchFlow.Tests;

public sealed class MatchFlowHostTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public MatchFlowHostTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.MatchFlow.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
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

    // The TS server's fixed answer (tools/ssc/constants_diff.mjs compares it byte for byte).
    [Fact]
    public async Task PerksAbsentAnswersTheTsServersFixedAnswer()
    {
        using var response = await _factory.CreateGameClient().PutAsync("/ssc/invoke/perks_absent",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"body":{"message":"Early absent report"},"metadata":null,"return_code":2}""", await response.Content.ReadAsStringAsync());
    }

    // Routes the TS server never handled: what the game always got (its catch-all; without Mongo the default CRC), with
    // any method.
    [Theory]
    [InlineData("GET", "check_training_server_ready")]
    [InlineData("PUT", "get_or_create_my_match_config")]
    [InlineData("POST", "sync_match_config")]
    [InlineData("DELETE", "sync_match_config")]
    public async Task RoutesNeverSeenAnswerTheTsCatchAll(string method, string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/ssc/invoke/{route}");
        if (method != "GET")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await _factory.CreateGameClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-OVS-Stub"));
        Assert.Equal("""{"body":{"Crc":1267552956,"MatchmakingCrc":1},"metadata":null,"return_code":200}""", await response.Content.ReadAsStringAsync());
    }

    // Answered whatever happened (here: no Redis, no Mongo), as the TS server answered.
    [Theory]
    [InlineData("perks_lock", """{"ContainerMatchId":"m","Perks":[]}""")]
    [InlineData("toast_player", """{"ContainerMatchId":"m","ToasteeId":"t"}""")]
    public async Task PerksLockAndToastAnswerEmpty(string route, string body)
    {
        using var response = await _factory.CreateGameClient().PutAsync($"/ssc/invoke/{route}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"body":{},"metadata":null,"return_code":0}""", await response.Content.ReadAsStringAsync());
    }

    // A rollback server's recording: no token needed (a rollback route), the MatchUpdateKey is; none is configured here.
    [Fact]
    public async Task MatchInputsWithoutAConfiguredKeyAreRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ovs_match_inputs")
        {
            Content = new StringContent("""{"matchId":"m","players":[]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("MatchUpdateKey", "anything");
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"Invalid signature"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Runs_the_match_result_subscribers_and_the_node_key_check_and_nothing_else_of_its_own()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        // NodeKeyCheck runs once at startup and returns: it logs whether the P2P node signing key is the expected one.
        Assert.Equal(["ClusterSettingsSync", "InstanceHeartbeat", "MatchResultStream", "NodeKeyCheck"], hosted);
    }

    [Fact]
    public void Is_the_match_flow_service()
    {
        Assert.Same(KnownServices.MatchFlow, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public async Task Is_alive_but_not_ready_without_Redis()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("REDIS is not set", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Its_readiness_depends_only_on_stores()
    {
        // Never on another service (ServiceStores): a readiness chain between services can wait forever.
        var ready = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations.Where(r => r.Tags.Contains("ready")).Select(r => r.Name).ToList();

        Assert.NotEmpty(ready);
        Assert.All(ready, name => Assert.Contains(name, new[] { "redis", "mongo" }));
    }
}
