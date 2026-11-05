using System.ComponentModel.DataAnnotations;
using OpenVersus.Server.Core.Matchmaking;
using Xunit;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>Ranked 1v1's population-based skill window (PopulationWindows): the rules, apart from Redis.</summary>
public sealed class PopulationWindowsTests
{
    private static readonly MatchmakingPopulationSettings Defaults = new();

    [Theory]
    [InlineData(5, PopulationTier.Quiet)]
    [InlineData(6, PopulationTier.Medium)]
    [InlineData(15, PopulationTier.Medium)]
    [InlineData(16, PopulationTier.Busy)]
    public void APoolsTierFollowsMediumFromAndBusyFrom(int pool, PopulationTier tier) => Assert.Equal(tier, PopulationWindows.TierOf(pool, Defaults));

    [Theory]
    // Quiet keeps the fixed window: 250 under 5 s, 500 under 10 s, anyone after.
    [InlineData(PopulationTier.Quiet, 4.9, 250)]
    [InlineData(PopulationTier.Quiet, 5, 500)]
    [InlineData(PopulationTier.Quiet, 10, double.PositiveInfinity)]
    // Medium: 100 to 10 s, 200 to 20 s, 400 to 30 s, anyone after.
    [InlineData(PopulationTier.Medium, 9.9, 100)]
    [InlineData(PopulationTier.Medium, 10, 200)]
    [InlineData(PopulationTier.Medium, 29.9, 400)]
    [InlineData(PopulationTier.Medium, 30, double.PositiveInfinity)]
    // Busy: 100 to 30 s, 200 to 45 s, 300 to 60 s, anyone after.
    [InlineData(PopulationTier.Busy, 29.9, 100)]
    [InlineData(PopulationTier.Busy, 44.9, 200)]
    [InlineData(PopulationTier.Busy, 59.9, 300)]
    [InlineData(PopulationTier.Busy, 60, double.PositiveInfinity)]
    public void EachTiersWindowWidensAtItsSteps(PopulationTier tier, double waited, double window) =>
        Assert.Equal(window, PopulationWindows.Window(PopulationWindows.StepsOf(tier, Defaults), waited));

    private static PopulationWindows.Side Side(PopulationTier tier, double waited) => new(tier, PopulationWindows.StepsOf(tier, Defaults), waited);

    [Fact]
    public void APairMustFitTheStricterOfItsTwoWindows()
    {
        // Busy at 50 s allows 300, but the newcomer (Medium, 5 s) only 100.
        Assert.False(PopulationWindows.Allows(Side(PopulationTier.Busy, 50), Side(PopulationTier.Medium, 5), 150));
        Assert.True(PopulationWindows.Allows(Side(PopulationTier.Busy, 50), Side(PopulationTier.Medium, 5), 100));
    }

    [Fact]
    // Without the cap fix, a capped player waited for a newcomer's window to open too (p90 waits ~100 s in the replay).
    public void APairMatchesWhateverTheGapOnceEitherHasWaitedItsTiersCap()
    {
        Assert.True(PopulationWindows.Allows(Side(PopulationTier.Medium, 30), Side(PopulationTier.Busy, 1), 2000));
        Assert.True(PopulationWindows.Allows(Side(PopulationTier.Busy, 1), Side(PopulationTier.Quiet, 10), 2000));
        Assert.False(PopulationWindows.Allows(Side(PopulationTier.Medium, 29), Side(PopulationTier.Busy, 1), 2000));
    }

    [Fact]
    public void APoolCountsThePlayersNearTheTicketsRpAndEveryUnknownOne()
    {
        double[] searching = [1000, 1250, 1301, 700];
        double[] backSoon = [1100, 1400, double.NaN];
        // Within 300 of 1000: 1000, 1250, 700 searching; 1100 and the unknown back soon.
        Assert.Equal(5, PopulationWindows.Pool(1000, searching, backSoon, 300));
    }

    [Theory]
    [InlineData("10:100,20:200", true)]
    [InlineData("5:250", true)]
    [InlineData("20:100,10:200", false)]
    [InlineData("10:100,10:200", false)]
    [InlineData("ten:100", false)]
    [InlineData("10:-1", false)]
    [InlineData("", false)]
    public void StepsMustBeSecondsColonRpWithTheSecondsRising(string text, bool valid) =>
        Assert.Equal(valid, PopulationWindows.Parse(text) is not null);

    [Fact]
    public void SettingsWithBadStepsOrTiersOutOfOrderAreRefused()
    {
        static bool Valid(MatchmakingPopulationSettings s) => Validator.TryValidateObject(s, new ValidationContext(s), [], validateAllProperties: true);
        Assert.True(Valid(new MatchmakingPopulationSettings()));
        Assert.False(Valid(new MatchmakingPopulationSettings { Medium = "30:100,10:200" }));
        Assert.False(Valid(new MatchmakingPopulationSettings { MediumFrom = 20, BusyFrom = 10 }));
    }
}
