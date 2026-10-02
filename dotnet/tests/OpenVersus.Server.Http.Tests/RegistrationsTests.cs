using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Http.Tests;

/// <summary>What the container does not check when it is built (see <see cref="Registrations"/>).</summary>
public sealed class RegistrationsTests : IClassFixture<GameAppFactory>
{
    private readonly GameAppFactory _factory;

    public RegistrationsTests(GameAppFactory factory)
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
    public void EveryServiceItsEndpointsLookUpIsRegistered()
    {
        var problems = Registrations.UnresolvableLookups(_factory.Services, typeof(Program).Assembly);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
