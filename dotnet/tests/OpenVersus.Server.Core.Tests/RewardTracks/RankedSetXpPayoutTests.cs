using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// Who End Game's set XP pays for a set C# rates (RankedSetXpPayout): nothing before a game is played, nothing for whoever
/// quit, everyone else. The rules of the TS rankedSetXpService.ts. Needs no stores.
/// </summary>
public sealed class RankedSetXpPayoutTests
{
    private static readonly Dictionary<string, string> s_characters = new() { ["w"] = "character_shaggy", ["l"] = "character_BananaGuard" };

    private static SetOutcome Set(int team0, int team1, bool concede = false, bool dodge = false, IReadOnlyList<string>? quitters = null,
        int gamesBefore = 0, IReadOnlyList<string>? losers = null) =>
        new(["w"], losers ?? ["l"], "1v1", team0, team1, 0, concede, s_characters, "set-1", dodge, quitters, gamesBefore);

    [Fact]
    public void ASetPlayedOutPaysBothSides() =>
        Assert.Equal([("w", true), ("l", false)], RankedSetXpPayout.Recipients(Set(2, 1)));

    [Fact]
    public void APregameDodgeBeforeAnyGamePaysNobody() =>
        Assert.Empty(RankedSetXpPayout.Recipients(Set(0, 0, concede: true, dodge: true, quitters: ["l"])));

    [Fact]
    public void ADodgeBeforeGameTwoPaysTheWinnerOnly() =>
        Assert.Equal([("w", true)], RankedSetXpPayout.Recipients(Set(0, 0, concede: true, dodge: true, quitters: ["l"], gamesBefore: 1)));

    [Fact]
    public void AConcedeAfterAGamePaysTheWinnerNotTheConceder() =>
        Assert.Equal([("w", true)], RankedSetXpPayout.Recipients(Set(1, 0, concede: true, quitters: ["l"])));

    [Fact]
    public void AConcedeNamingNobodyCountsEveryLoserAsAQuitter() =>
        Assert.Equal([("w", true)], RankedSetXpPayout.Recipients(Set(1, 0, concede: true)));

    [Fact]
    public void A2v2TeammateWhoStayedIsPaid() =>
        Assert.Equal([("w", true), ("stayed", false)], RankedSetXpPayout.Recipients(Set(1, 0, concede: true, quitters: ["l"], losers: ["l", "stayed"])));
}
