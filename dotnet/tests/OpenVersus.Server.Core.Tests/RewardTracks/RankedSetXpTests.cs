using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// End Game's ranked-set XP (RankedSetXpSubscriber): what a set is worth to each track, and CurrentTier stopping at a
/// finished track's last tier. Needs no stores.
/// </summary>
public sealed class RankedSetXpTests
{
    [Fact]
    public void ASetPaysTheBattlePassTheAccountAndTheCharacterPlayed()
    {
        var settings = new RewardTrackSettings();
        Assert.Equal(new Dictionary<string, int>
        {
            ["mrt_battlepass_season_five"] = 450,
            ["mrt_mastery_account"] = 600,
            ["mrt_mastery_banana_guard"] = 600,
        }, RankedSetXpSubscriber.Points(settings, won: true, "character_BananaGuard"));
        // A loss: no win bonus.
        Assert.Equal(new Dictionary<string, int>
        {
            ["mrt_battlepass_season_five"] = 300,
            ["mrt_mastery_account"] = 400,
            ["mrt_mastery_banana_guard"] = 400,
        }, RankedSetXpSubscriber.Points(settings, won: false, "character_BananaGuard"));
    }

    [Fact]
    public void ACharacterWithoutALevelTrackPaysOnlyThePassAndTheAccount()
    {
        var points = RankedSetXpSubscriber.Points(new RewardTrackSettings(), won: false, "character_nobody");
        Assert.Equal(["mrt_battlepass_season_five", "mrt_mastery_account"], points.Keys.Order());
    }

    [Fact]
    public void TheSettingsSetTheAmounts()
    {
        var settings = new RewardTrackSettings { BattlePassSetXp = 10, BattlePassWinXp = 5, CharacterSetXp = 0, CharacterWinXp = 0 };
        Assert.Equal(new Dictionary<string, int> { ["mrt_battlepass_season_five"] = 15 },
            RankedSetXpSubscriber.Points(settings, won: true, "character_BananaGuard"));
    }

    [Fact]
    public void AFinishedTrackStopsAtItsLastTier()
    {
        const string pass = "mrt_battlepass_season_five";
        var tiers = (JsonArray)HissTables.Data("milestone-reward-tracks", pass)!["Tiers"]!;
        double last = tiers.OfType<JsonObject>().Max(t => t["ScoreThreshold"]!.GetValue<double>());
        var finished = RewardTrackService.Scored(pass, RewardTrackService.Initial(pass), (long)last + 100_000);
        Assert.Equal(tiers.Count, finished["CompletedTiers"]!.AsArray().Count);
        Assert.Equal(tiers.Count - 1, finished["CurrentTier"]!.GetValue<int>());
    }
}
