using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Matchmaking.Tests;

public sealed class MatchmakingHostTests : IClassFixture<MatchmakingHostTests.Factory>
{
    private readonly Factory _factory;

    public MatchmakingHostTests(Factory factory)
    {
        _factory = factory;
    }

    /// <summary>Development: the container validates every registration when the host is built.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Development);
    }

    [Fact]
    public void Runs_the_matchmaking_worker_and_nothing_else_of_its_own()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.Equal(["ClusterSettingsSync", "MatchmakingWorker"], hosted);
    }

    [Fact]
    public void Is_the_matchmaking_service()
    {
        Assert.Same(KnownServices.Matchmaking, _factory.Services.GetRequiredService<ServiceDefinition>());
    }

    [Fact]
    public async Task Answers_health()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
