using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The HTTP service answers the game; workers are their own executables (the matchmaker, the match flow, and later the
/// websocket), so that a deploy of one never restarts another. This list is what still runs in the HTTP service:
/// a background service added here has to be a decision, not a default.
/// </summary>
public sealed class HostedServicesTests : IClassFixture<GameAppFactory>
{
    private readonly GameAppFactory _factory;

    public HostedServicesTests(GameAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Runs_only_the_listed_background_services()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        // Each per process by nature: the bans this process checks, its share of the cluster settings, its hiss tables,
        // its heartbeat into the instance registry.
        Assert.Equal(["BanLoader", "ClusterSettingsSync", "HissWarmup", "InstanceHeartbeat"], hosted);
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
