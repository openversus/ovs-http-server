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

    [Theory]
    // The TS server compares ON_DEMAND_ROLLBACK === 1 (envalid's num): 1 is on, any other number off.
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("2", "false")]
    // Not a number: passed on, and refused at startup as envalid refuses it.
    [InlineData("yes", "yes")]
    public void OnDemandRollbackReadsAsTheTsServerCompares(string value, string expected) =>
        Assert.Equal(expected, With(new() { ["ON_DEMAND_ROLLBACK"] = value })["Rollback:OnDemand"]);

    [Theory]
    // P2P_ROLLBACK is compared the same way (env.P2P_ROLLBACK !== 1 in src/p2p.ts).
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("2", "false")]
    public void P2PRollbackReadsAsTheTsServerCompares(string value, string expected) =>
        Assert.Equal(expected, With(new() { ["P2P_ROLLBACK"] = value })["Rollback:P2P"]);

    [Fact]
    public void TheCatalogKeyWins() =>
        Assert.Equal("2.0", With(new() { ["MIN_CLIENT_VERSION"] = "1.0", ["Clients:MinimumVersion"] = "2.0" })["Clients:MinimumVersion"]);

    [Fact]
    public void OtherNamesAreCopiedAsTheyAre() =>
        Assert.Equal("195303.1.1", With(new() { ["GAME_VERSION"] = "195303.1.1" })["Lobbies:GameVersion"]);
}
