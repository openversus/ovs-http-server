using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Social.Tests;

public sealed class SocialServiceTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public SocialServiceTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.Social.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void IsTheSocialService()
    {
        Assert.Same(KnownServices.Social, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public void RunsNoBackgroundServiceOfItsOwn()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true).Select(t => t.Name).Order().ToList();

        Assert.Equal(["ClusterSettingsSync", "InstanceHeartbeat"], hosted);
    }

    [Fact]
    public void ItsReadinessDependsOnlyOnStores()
    {
        var ready = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(r => r.Tags.Contains("ready")).Select(r => r.Name).ToList();

        Assert.NotEmpty(ready);
        Assert.All(ready, name => Assert.Contains(name, new[] { "redis", "mongo" }));
    }
}
