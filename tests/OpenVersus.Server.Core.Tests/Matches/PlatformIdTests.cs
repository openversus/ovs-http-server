using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>The id a lobby entry gives under Steam: Steam, else Epic, else the account id; never hardware or install ids.</summary>
public sealed class PlatformIdTests
{
    private const string Account = "0000000000000000000a0001";

    private static Dictionary<string, string> Session(params (string Field, string Value)[] fields) => fields.ToDictionary(f => f.Field, f => f.Value);

    [Fact]
    public void SteamFirst() =>
        Assert.Equal("76561198000000001", PartyLobbyService.PlatformId(Account, Session(("steamId", "76561198000000001"), ("epicId", "epic1"))));

    [Fact]
    public void EpicWithoutSteam() => Assert.Equal("epic1", PartyLobbyService.PlatformId(Account, Session(("epicId", "epic1"))));

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ip_198.51.100.3")]
    public void PlaceholdersAreNoId(string steamId) =>
        Assert.Equal("epic1", PartyLobbyService.PlatformId(Account, Session(("steamId", steamId), ("epicId", "epic1"))));

    [Fact]
    public void TheTokenWhenTheSessionHasNone() =>
        Assert.Equal("76561198000000002", PartyLobbyService.PlatformId(Account, Session(), new LobbyPlayer(Account, "h", "n", Account, SteamId: "76561198000000002")));

    [Fact]
    public void NeverTheHardwareOrInstallId() =>
        Assert.Equal(Account, PartyLobbyService.PlatformId(Account, Session(("steamId", "Unknown"), ("hardwareId", "hw"), ("installId", "install"))));
}
