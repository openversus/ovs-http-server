using Microsoft.Extensions.Configuration;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Tests.Settings;

/// <summary>The TS server's environment names filling catalog keys.</summary>
public sealed class TsEnvironmentTests
{
    private static IConfiguration With(Dictionary<string, string?> environment)
    {
        var current = new ConfigurationBuilder().AddInMemoryCollection(environment).Build();
        var builder = new ConfigurationBuilder().AddInMemoryCollection(environment);
        TsEnvironment.AddAliases(builder, current);
        return builder.Build();
    }

    [Theory]
    // envalid's bool spellings, which .NET's binding would refuse, become true / false.
    [InlineData("t", "true")]
    [InlineData("1", "true")]
    [InlineData("true", "true")]
    [InlineData("f", "false")]
    [InlineData("0", "false")]
    [InlineData("false", "false")]
    // Anything else is passed on, and refused at startup as the TS server refuses it.
    [InlineData("yes", "yes")]
    public void ClientVersionCheckReadsAsEnvalidDoes(string value, string expected) =>
        Assert.Equal(expected, With(new() { ["CLIENT_VERSION_CHECK"] = value })["Clients:VersionCheck"]);

    [Fact]
    public void TheCatalogKeyWins() =>
        Assert.Equal("2.0", With(new() { ["MIN_CLIENT_VERSION"] = "1.0", ["Clients:MinimumVersion"] = "2.0" })["Clients:MinimumVersion"]);

    [Fact]
    public void OtherNamesAreCopiedAsTheyAre() =>
        Assert.Equal("195303.1.1", With(new() { ["GAME_VERSION"] = "195303.1.1" })["Lobbies:GameVersion"]);
}
