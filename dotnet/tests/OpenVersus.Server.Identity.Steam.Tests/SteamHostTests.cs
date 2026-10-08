using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Identity.Steam.Tests;

public sealed class SteamHostTests : IClassFixture<ServiceFactory<Program>>
{
    private readonly ServiceFactory<Program> _factory;

    public SteamHostTests(ServiceFactory<Program> factory)
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
    public void Runs_the_session_machine_and_nothing_else_of_its_own()
    {
        var hosted = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace?.StartsWith("OpenVersus.", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Order()
            .ToList();

        Assert.Equal(["BannedPlayers", "ClusterSettingsSync", "InstanceHeartbeat", "SteamAuthSessions"], hosted);
    }

    [Fact]
    public void Is_the_steam_service_with_no_public_port()
    {
        var service = _factory.Services.GetRequiredService<ServiceDefinition>();
        Assert.Same(KnownServices.Steam, service);
        Assert.Null(service.PublicPortKey);
    }

    [Fact]
    public void Talks_to_Steam_through_SteamKit()
    {
        Assert.IsType<SteamKitAuthClient>(_factory.Services.GetRequiredService<ISteamAuthClient>());
    }
}
