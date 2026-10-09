using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// 1v1 Testing Grounds (Beta Speed) takes FFA's place on weekdays: open exactly while the FFA queue is closed, and the
/// hiss shows the open one only. Needs no stores.
/// </summary>
public sealed class TestingGroundsTests
{
    [Theory]
    // A Wednesday afternoon in New York: Testing Grounds; a Saturday: FFA; Sunday 23:59 Eastern (Monday 03:59 UTC, EDT):
    // FFA has closed, Testing Grounds is open.
    [InlineData("2026-10-07T18:00:00Z", true)]
    [InlineData("2026-10-03T15:00:00Z", false)]
    [InlineData("2026-10-05T03:58:59Z", false)]
    [InlineData("2026-10-05T03:59:00Z", true)]
    [InlineData("2026-10-02T04:00:00Z", false)]
    public void OpenWhileTheFfaQueueIsClosed(string now, bool open)
    {
        var at = DateTimeOffset.Parse(now);
        Assert.Equal(open, TestingGrounds.IsOpen(at, enabled: true, ffaWeekendOnly: true));
        Assert.NotEqual(open, FfaSchedule.IsOpen(at, weekendOnly: true));
    }

    [Fact]
    public void NeverOpenWhenDisabledOrWhileFfaIsOpenAllWeek()
    {
        var wednesday = DateTimeOffset.Parse("2026-10-07T18:00:00Z");
        Assert.False(TestingGrounds.IsOpen(wednesday, enabled: false, ffaWeekendOnly: true));
        Assert.False(TestingGrounds.IsOpen(wednesday, enabled: true, ffaWeekendOnly: false));
        Assert.True(new TestingGroundsSettings().Enabled);
    }

    [Fact]
    public void AlwaysOpenTakesTheWeekendToo()
    {
        var saturday = DateTimeOffset.Parse("2026-10-03T15:00:00Z");
        Assert.True(TestingGrounds.IsOpen(saturday, enabled: true, ffaWeekendOnly: true, alwaysOpen: true));
        Assert.False(TestingGrounds.IsOpen(saturday, enabled: false, ffaWeekendOnly: true, alwaysOpen: true));
        Assert.False(new TestingGroundsSettings().AlwaysOpen);
    }

    [Fact]
    public void ItsMatchesRunBetaSpeedUnranked()
    {
        var config = TestingGrounds.ConfigOverride();
        Assert.False(config["bIsCustomGame"]!.GetValue<bool>());
        // No XP and no missions (MissionService reads it), whatever RewardTracks:MatchXp says.
        Assert.False(config["bModeGrantsProgress"]!.GetValue<bool>());
        Assert.Equal("evtq_1v1testinggrounds", config["EventQueueSlug"]!.GetValue<string>());
        Assert.Equal(["ovs_beta_speed"], config["WorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal(TestingGrounds.Mutator, OpenVersus.Server.Core.Matches.GameplayConfigs.BetaSpeedMutator);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHissShowsTheOpenQueueOnly(bool open)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        TestingGrounds.ApplyTo(answer, open);
        var data = answer["body"]!["Data"]!;
        var toggles = data["feature-toggles"]!["_hydra_compressed"]!;
        Assert.Equal(open, toggles["evtq_1v1testinggrounds"]!.GetValue<bool>());
        Assert.Equal(!open, toggles["FFA"]!.GetValue<bool>());
        Assert.Equal(open, data["event-queue-config"]!["_hydra_compressed"]!["evtq_1v1testinggrounds"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        var mode = data["game-mode-config"]!["_hydra_compressed"]!["gm_1v1shields"]!["data"]!;
        Assert.Equal(["ovs_beta_speed"], mode["GameModeData"]!["RequiredWorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        // The 2v2 queue stays off either way.
        Assert.False(toggles["evtq_2v2testinggrounds"]!.GetValue<bool>());
    }
}
