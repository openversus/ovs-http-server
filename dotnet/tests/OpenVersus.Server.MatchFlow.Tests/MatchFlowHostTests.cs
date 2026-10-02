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

    [Fact]
    public void Runs_the_match_result_subscribers_and_nothing_else_of_its_own()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.Equal(["ClusterSettingsSync", "InstanceHeartbeat", "MissionResultSubscriber", "RiftResultSubscriber"], hosted);
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
