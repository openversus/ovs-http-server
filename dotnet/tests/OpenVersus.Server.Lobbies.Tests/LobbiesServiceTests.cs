using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Lobbies.Tests;

public sealed class LobbiesServiceTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public LobbiesServiceTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.Lobbies.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void IsTheLobbiesService()
    {
        Assert.Same(KnownServices.Lobbies, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public void RunsTheRematchTimerAndNoOtherBackgroundServiceOfItsOwn()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true).Select(t => t.Name).Order().ToList();

        // RematchSweep starts a rematch whose vote is still open when its timer runs out (rematch:due).
        Assert.Equal(["ClusterSettingsSync", "InstanceHeartbeat", "RematchSweep"], hosted);
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

    // The rift lobby is made from the frozen runtime data; load_rifts and start_rift_node, the HTTP service's, are not here.
    [Fact]
    public void WarnsOnlyAboutTheFrozenDataItServes()
    {
        Assert.Equal(["PUT /ssc/invoke/create_rift_lobby"], _factory.Services.GetServices<FrozenAccountData>().Select(f => f.Response));
    }
}
