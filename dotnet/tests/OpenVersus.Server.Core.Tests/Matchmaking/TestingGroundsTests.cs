using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// 1v1 Testing Grounds (OpenVersus's mode: Beta Speed + Shield HP) takes FFA's place on weekdays: open exactly while the
/// FFA queue is closed, the hiss shows the open one only, and a player without the mode's pak, or whose game runs another
/// Beta Speed, is refused. Needs no stores.
/// </summary>
public sealed class TestingGroundsTests
{
    [Theory]
    // The days FFA is not (2026-10-16: Monday, Wednesday, Friday and Sunday, New York time): Wednesday, Friday 00:00 and a
    // Sunday are Testing Grounds; a Saturday is FFA; Monday 23:59:59 is still Testing Grounds, Tuesday 00:00 FFA (EDT).
    [InlineData("2026-10-07T18:00:00Z", true)]
    [InlineData("2026-10-02T04:00:00Z", true)]
    [InlineData("2026-10-04T15:00:00Z", true)]
    [InlineData("2026-10-03T15:00:00Z", false)]
    [InlineData("2026-10-06T03:59:59Z", true)]
    [InlineData("2026-10-06T04:00:00Z", false)]
    public void OpenWhileTheFfaQueueIsClosed(string now, bool open)
    {
        var at = DateTimeOffset.Parse(now);
        Assert.Equal(open, TestingGrounds.IsOpen(at, enabled: true, new FfaSettings()));
        Assert.NotEqual(open, FfaSchedule.IsOpen(at, new FfaSettings()));
    }

    [Fact]
    public void TheClosedAnswerNamesItsDaysAndFfas() => Assert.Equal(
        "1v1 Testing Grounds is open Monday, Wednesday, Friday and Sunday (US Eastern), except during Twosday; Free For All takes its place on Tuesday, Thursday and Saturday.",
        (string?)TestingGrounds.ClosedFailure(new FfaSettings())["body"]!["ErrorMessage"]);

    [Fact]
    public void NeverOpenWhenDisabledOrWhileFfaIsOpenAllWeek()
    {
        var wednesday = DateTimeOffset.Parse("2026-10-07T18:00:00Z");
        Assert.False(TestingGrounds.IsOpen(wednesday, enabled: false, new FfaSettings()));
        Assert.False(TestingGrounds.IsOpen(wednesday, enabled: true, new FfaSettings { WeekendOnly = false }));
        Assert.True(new TestingGroundsSettings().Enabled);
    }

    [Fact]
    public void AlwaysOpenTakesTheWeekendToo()
    {
        var saturday = DateTimeOffset.Parse("2026-10-03T15:00:00Z");
        Assert.True(TestingGrounds.IsOpen(saturday, enabled: true, new FfaSettings(), alwaysOpen: true));
        Assert.False(TestingGrounds.IsOpen(saturday, enabled: false, new FfaSettings(), alwaysOpen: true));
        Assert.False(new TestingGroundsSettings().AlwaysOpen);
    }

    [Fact]
    public void ItsMatchesRunBetaSpeedAndShieldHpUnranked()
    {
        var config = TestingGrounds.ConfigOverride(new TestingGroundsSettings());
        Assert.False(config["bIsCustomGame"]!.GetValue<bool>());
        // No XP and no missions (MissionService reads it), whatever RewardTracks:MatchXp says.
        Assert.False(config["bModeGrantsProgress"]!.GetValue<bool>());
        Assert.Equal("evtq_1v1testinggrounds", config["EventQueueSlug"]!.GetValue<string>());
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp"], config["WorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Contains(OpenVersus.Server.Core.Matches.GameplayConfigs.BetaSpeedMutator, TestingGrounds.MutatorsOf(new TestingGroundsSettings()));
    }

    [Fact]
    public void ItsMutatorsComeFromTheSettings()
    {
        var settings = new TestingGroundsSettings { Mutators = " ovs_beta_speed , ,ovs_friendly_fire,ovs_beta_speed" };
        Assert.Equal(["ovs_beta_speed", "ovs_friendly_fire"], TestingGrounds.MutatorsOf(settings));
        Assert.Empty(TestingGrounds.MutatorsOf(new TestingGroundsSettings { Mutators = "" }));
    }

    [Fact]
    public void WithBetaSpeedAPlayerMustRunTheServersSpeed()
    {
        var settings = new TestingGroundsSettings();
        var betaSpeed = new OpenVersus.Server.Core.Matches.BetaSpeedSettings { Percent = 120 };
        Assert.False(TestingGrounds.WrongBetaSpeed(settings, betaSpeed, "120"));
        // Another speed (read before a change), or none (an older client).
        Assert.True(TestingGrounds.WrongBetaSpeed(settings, betaSpeed, "110"));
        Assert.True(TestingGrounds.WrongBetaSpeed(settings, betaSpeed, null));
        // Without the mutator the speed does not matter.
        Assert.False(TestingGrounds.WrongBetaSpeed(new TestingGroundsSettings { Mutators = "ovs_shield_hp" }, betaSpeed, null));
        var failure = TestingGrounds.BetaSpeedFailure();
        Assert.Equal(1, failure["return_code"]!.GetValue<int>());
        Assert.Equal("BetaSpeedChanged", failure["body"]!["ErrorCode"]!.GetValue<string>());
    }

    [Fact]
    // Tuggernuts, 2026-10-13: during Twosday neither tile shows; FFA only while its queue is open (2026-10-16: Tuesday is
    // an FFA day, closed for Twosday's hours).
    public void TheFfaTileShowsOnlyWhileTheFfaQueueIsOpen()
    {
        var ffa = new FfaSettings();
        var saturday = new DateTimeOffset(2026, 10, 17, 20, 0, 0, TimeSpan.FromHours(-4));
        var tuesday = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(-4));
        Assert.True(TestingGrounds.FfaTileShown(saturday, testingGroundsOpen: false, enabled: true, ffa));
        // Tuesday: FFA's day; during Twosday no tile at all.
        Assert.True(TestingGrounds.FfaTileShown(tuesday, testingGroundsOpen: false, enabled: true, ffa));
        Assert.False(TestingGrounds.FfaTileShown(tuesday, testingGroundsOpen: false, enabled: true, ffa, twosday: true));
        Assert.False(TestingGrounds.FfaTileShown(tuesday, testingGroundsOpen: true, enabled: true, ffa, twosday: true));
        // Testing Grounds off: FFA shows all week, as before it existed.
        Assert.True(TestingGrounds.FfaTileShown(tuesday, testingGroundsOpen: false, enabled: false, ffa, twosday: true));
        // FFA every day: its tile whenever Testing Grounds is not open.
        Assert.True(TestingGrounds.FfaTileShown(tuesday, testingGroundsOpen: false, enabled: true, new FfaSettings { WeekendOnly = false }, twosday: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheHissShowsTheOpenQueueOnly(bool open, bool ffaTile)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        TestingGrounds.ApplyTo(answer, open, new TestingGroundsSettings(), ffaTile);
        var data = answer["body"]!["Data"]!;
        var toggles = data["feature-toggles"]!["_hydra_compressed"]!;
        Assert.Equal(open, toggles["evtq_1v1testinggrounds"]!.GetValue<bool>());
        Assert.Equal(ffaTile, toggles["FFA"]!.GetValue<bool>());
        Assert.Equal(ffaTile, toggles["evtq_ffa"]!.GetValue<bool>());
        Assert.Equal(ffaTile, data["event-queue-config"]!["_hydra_compressed"]!["evtq_ffa"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        Assert.Equal(open, data["event-queue-config"]!["_hydra_compressed"]!["evtq_1v1testinggrounds"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        var mode = data["game-mode-config"]!["_hydra_compressed"]!["gm_1v1shields"]!["data"]!;
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp"], mode["GameModeData"]!["RequiredWorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        // The 2v2 queue stays off either way.
        Assert.False(toggles["evtq_2v2testinggrounds"]!.GetValue<bool>());
    }
}
