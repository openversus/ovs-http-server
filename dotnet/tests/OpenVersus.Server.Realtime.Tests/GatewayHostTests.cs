using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Realtime.Tests;

public sealed class GatewayHostTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public GatewayHostTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void EverySettingItsServicesReadIsBound()
    {
        var problems = Registrations.UnboundOptions(_factory.Registered, _factory.Services);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryServiceItLooksUpIsRegistered()
    {
        var problems = Registrations.UnresolvableLookups(_factory.Services, typeof(Program).Assembly, _factory.Registered);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Runs_the_pings_the_matchmaking_ticks_and_the_delivery_and_nothing_else_of_its_own()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.Equal(["ClusterSettingsSync", "GatewayPings", "GatewaySubscriber", "GatewayTicks", "InstanceHeartbeat"], hosted);
    }

    [Fact]
    public async Task Answers_plain_requests_as_the_TS_websocket_server_did()
    {
        var answer = await _factory.CreateClient().GetAsync("/anything");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("text/plain", answer.Content.Headers.ContentType?.ToString());
        Assert.Equal("HTTP server is running\n", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Is_the_realtime_service()
    {
        Assert.Same(KnownServices.Realtime, _factory.Services.GetRequiredService<ServiceDefinition>());
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
