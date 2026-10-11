using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Arenas.Tests;

public sealed class ArenasServiceTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public ArenasServiceTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.Arenas.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void IsTheArenasService()
    {
        Assert.Same(KnownServices.Arenas, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public void RunsTheArenaDeadlinesAndNoOtherBackgroundServiceOfItsOwn()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true).Select(t => t.Name).Order().ToList();

        // ArenaDeadlines acts on the runs whose character select or shop has run out of time (arena_run:deadlines).
        Assert.Equal(["ArenaDeadlines", "BannedPlayers", "ClusterSettingsSync", "InstanceHeartbeat"], hosted);
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
}
