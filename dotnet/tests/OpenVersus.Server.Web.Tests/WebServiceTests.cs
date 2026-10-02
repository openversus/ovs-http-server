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
