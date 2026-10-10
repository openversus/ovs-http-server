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

    [Theory]
    // FFA_WEEKEND_ONLY is envalid's bool too (src/env/env.ts).
    [InlineData("f", "false")]
    [InlineData("0", "false")]
    [InlineData("true", "true")]
    public void FfaWeekendOnlyReadsAsEnvalidDoes(string value, string expected) =>
        Assert.Equal(expected, With(new() { ["FFA_WEEKEND_ONLY"] = value })["Ffa:WeekendOnly"]);

    [Theory]
    // MISSIONS_ENABLED is envalid's bool too (src/env/env.ts).
    [InlineData("t", "true")]
    [InlineData("0", "false")]
    public void MissionsEnabledReadsAsEnvalidDoes(string value, string expected) =>
        Assert.Equal(expected, With(new() { ["MISSIONS_ENABLED"] = value })["Missions:Enabled"]);

    [Theory]
    // The rating knobs a TS .env names: production's .env carries every one of them.
    [InlineData("K_1V1", "Ranked:K1v1")]
    [InlineData("K_2V2", "Ranked:K2v2")]
    [InlineData("K_PROVISIONAL", "Ranked:KProvisional")]
    [InlineData("PROVISIONAL_GAME_THRESHOLD", "Ranked:ProvisionalSets")]
    [InlineData("ELO_DIVISOR", "Ranked:EloDivisor")]
    [InlineData("DEFAULT_ELO", "Ranked:DefaultElo")]
    public void TheRatingKnobsCarryOver(string tsName, string key) => Assert.Equal("24", With(new() { [tsName] = "24" })[key]);

    [Fact]
    public void TheAdminPasswordAndTheAssetTokenCarryOver()
    {
        // Production's .env names them so; without the alias the admin pages would keep Admin:Password's default.
        var config = With(new() { ["ADMIN_PASSWORD"] = "from-env", ["DATA_ASSET_TOKEN"] = "token-from-env" });
        Assert.Equal("from-env", config["Admin:Password"]);
        Assert.Equal("token-from-env", config["Admin:DataAssetToken"]);
    }

    [Fact]
    public void TheCatalogKeyWins() =>
        Assert.Equal("2.0", With(new() { ["MIN_CLIENT_VERSION"] = "1.0", ["Clients:MinimumVersion"] = "2.0" })["Clients:MinimumVersion"]);

    [Fact]
    public void OtherNamesAreCopiedAsTheyAre() =>
        Assert.Equal("195303.1.1", With(new() { ["GAME_VERSION"] = "195303.1.1" })["Lobbies:GameVersion"]);
}
