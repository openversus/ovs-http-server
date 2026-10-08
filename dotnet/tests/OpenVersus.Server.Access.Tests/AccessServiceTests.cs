using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Access.Tests;

public sealed class AccessServiceTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public AccessServiceTests(ServiceFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnswersExactlyTheRoutesItOwns()
    {
        var problems = await RouteOwnership.ProblemsAsync(_factory.CreateGameClient(), KnownServices.Access.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("POST", "/access")]
    [InlineData("DELETE", "/access")]
    [InlineData("POST", "/sessions/auth/token")]
    public async Task ItsLoginRoutesNeedNoSessionToken(string method, string path)
    {
        // The TS server answers them before its token check: they are how a game gets one.
        using var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void WarnsAboutTheCapturedAccountDataItServes()
    {
        var responses = _factory.Services.GetServices<FrozenAccountData>().Select(f => f.Response).Order().ToList();

        Assert.Equal(["POST /access", "POST /sessions/auth/token"], responses);
    }

    [Fact]
    public void ReadsTheCurrentSeasonFromItsConfiguration()
    {
        // The login reads the current season; without the setting registered here it would read the default unseen.
        using var app = _factory.WithWebHostBuilder(b => b.UseSetting("Seasons:Current", "Season:SeasonTest"));

        Assert.Equal("Season:SeasonTest", app.Services.GetRequiredService<IOptionsMonitor<OpenVersus.Server.Core.Seasons.SeasonSettings>>().CurrentValue.Current);
    }

    [Fact]
    public void IsTheAccessService()
    {
        Assert.Same(KnownServices.Access, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public void RunsTheBanLoaderTheBanSweepAndTheDailyToastPopupsAndNoOtherBackgroundServiceOfItsOwn()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true).Select(t => t.Name).Order().ToList();

        Assert.Equal(["BanLoader", "BanSweep", "BannedPlayers", "ClusterSettingsSync", "DailyToastPopups", "InstanceHeartbeat"], hosted);
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
