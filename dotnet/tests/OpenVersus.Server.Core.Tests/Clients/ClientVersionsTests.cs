using OpenVersus.Server.Core.Clients;

namespace OpenVersus.Server.Core.Tests.Clients;

/// <summary>ClientVersions against what the TS server's clientVersion.ts gives for the same inputs.</summary>
public sealed class ClientVersionsTests
{
    [Theory]
    [InlineData("2026.09.28.4", "2026.09.28.1", 1, false)]
    [InlineData("2026.09.28", "2026.09.28.0", 0, false)]
    [InlineData("v2026.9.1", "2026.09.01", 0, false)]
    [InlineData(" 2026.10.1-beta ", "2026.9.30", 1, false)]
    [InlineData("1.2.3.4.5", "1.2", 0, true)]
    [InlineData("abc", "1.0", 0, true)]
    [InlineData("", "1.0", 0, true)]
    [InlineData("2026.09.01.1", "2026.09.28.1", -1, true)]
    [InlineData("V3", "2.9.9.9", 1, false)]
    // JavaScript's \d is ASCII only: Arabic-Indic digits are not a version.
    [InlineData("١.٢", "1.2", 0, true)]
    public void AsTheTsServerComparesThem(string version, string minimum, int compare, bool updateRequired)
    {
        Assert.Equal(compare, ClientVersions.Compare(version, minimum));
        Assert.Equal(updateRequired, ClientVersions.UpdateRequired(version, minimum));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "  ")]
    [InlineData("1.0", "junk")]
    public void NoMinimumRequiresNoUpdate(string version, string minimum) => Assert.False(ClientVersions.UpdateRequired(version, minimum));

    [Theory]
    // The check on, no minimum: a registered client plays, an unregistered one does not (the default configuration).
    [InlineData("", "", true, true, false)]
    [InlineData("", "", false, true, true)]
    // The check off: anyone plays.
    [InlineData("1.0", "2.0", true, false, false)]
    [InlineData("1.0", "2.0", true, true, true)]
    public void GameplayAccess(string version, string minimum, bool registered, bool check, bool required) =>
        Assert.Equal(required, ClientVersions.GameplayAccessRequired(version, minimum, registered, check));
}
