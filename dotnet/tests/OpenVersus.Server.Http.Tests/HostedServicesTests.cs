using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The HTTP service answers the game; workers are their own executables (the matchmaker, and later the websocket and
/// match flow), so that a deploy of one never restarts another. This list is what still runs in the HTTP service:
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

        // Per process by nature: BanLoader, ClusterSettingsSync, HissWarmup. Moving to the match flow executable:
        // MissionResultSubscriber, RiftResultSubscriber.
        Assert.Equal(["BanLoader", "ClusterSettingsSync", "HissWarmup", "MissionResultSubscriber", "RiftResultSubscriber"], hosted);
    }
}
