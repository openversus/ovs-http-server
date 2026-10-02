using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.MatchFlow.Tests;

public sealed class MatchFlowHostTests : IClassFixture<MatchFlowHostTests.Factory>
{
    private readonly Factory _factory;

    public MatchFlowHostTests(Factory factory)
    {
        _factory = factory;
    }

    /// <summary>Development: the container validates every registration when the host is built.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Development);
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
