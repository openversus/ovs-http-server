using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// Testing Grounds (OpenVersus's modes): 1v1 (Beta Speed + Shield HP) every day, 2v2 (those plus Individual Stocks and
/// Friendly Fire) in FFA's place on the days FFA is not, neither during Twosday; the hiss shows the open ones only, and a
/// player whose game runs another Beta Speed is refused. Needs no stores.
/// </summary>
public sealed class TestingGroundsTests
{
    private static readonly TestingGroundsSettings s_on = new();

    [Theory]
    // The days FFA is not (Monday, Wednesday, Friday and Sunday, New York time) are 2v2's: Wednesday, Friday 00:00 and a
    // Sunday; a Saturday is FFA's; Monday 23:59:59 is still 2v2's, Tuesday 00:00 FFA's (EDT). 1v1 is open on every one.
    [InlineData("2026-10-07T18:00:00Z", true)]
    [InlineData("2026-10-02T04:00:00Z", true)]
    [InlineData("2026-10-04T15:00:00Z", true)]
    [InlineData("2026-10-03T15:00:00Z", false)]
    [InlineData("2026-10-06T03:59:59Z", true)]
    [InlineData("2026-10-06T04:00:00Z", false)]
    public void TwoVsTwoTakesFfasPlaceAndOneVsOneIsOpenEveryDay(string now, bool twoVsTwo)
    {
        var at = DateTimeOffset.Parse(now);
        var schedule = TestingGrounds.ScheduleAt(at, s_on, new FfaSettings());
        Assert.True(schedule.OneVsOne);
        Assert.Equal(twoVsTwo, schedule.TwoVsTwo);
        Assert.NotEqual(twoVsTwo, schedule.FfaTile);
        Assert.NotEqual(twoVsTwo, FfaSchedule.IsOpen(at, new FfaSettings()));
    }

    [Fact]
    // Tuggernuts, 2026-10-27: during Twosday everything but 2v2 closes, Testing Grounds and FFA alike.
    public void TwosdayClosesEveryQueueTileUnlessAlwaysOpen()
    {
        var tuesday = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(-4));
        var wednesday = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(new TestingGrounds.Schedule(false, false, false), TestingGrounds.ScheduleAt(tuesday, s_on, new FfaSettings(), twosday: true));
        // Twosday moved onto a 2v2 day by its hash: closed all the same.
        Assert.Equal(new TestingGrounds.Schedule(false, false, false), TestingGrounds.ScheduleAt(wednesday, s_on, new FfaSettings(), twosday: true));
        // Always open: both Testing Grounds queues, so no FFA tile.
        Assert.Equal(new TestingGrounds.Schedule(true, true, false),
            TestingGrounds.ScheduleAt(tuesday, new TestingGroundsSettings { AlwaysOpen = true }, new FfaSettings(), twosday: true));
        Assert.False(s_on.AlwaysOpen);
    }

    [Fact]
    public void OffSwitches()
    {
        var wednesday = DateTimeOffset.Parse("2026-10-07T18:00:00Z");
        var saturday = DateTimeOffset.Parse("2026-10-03T15:00:00Z");
        // Testing Grounds off: neither queue, and FFA's tile all week, as before Testing Grounds existed.
        Assert.Equal(new TestingGrounds.Schedule(false, false, true), TestingGrounds.ScheduleAt(wednesday, new TestingGroundsSettings { Enabled = false }, new FfaSettings()));
        // 2v2 off: 1v1 stays, FFA's tile shows all week.
        Assert.Equal(new TestingGrounds.Schedule(true, false, true), TestingGrounds.ScheduleAt(wednesday, new TestingGroundsSettings { TwoVsTwoEnabled = false }, new FfaSettings()));
        // FFA every day: 2v2 never opens, FFA's tile always shows.
        Assert.Equal(new TestingGrounds.Schedule(true, false, true), TestingGrounds.ScheduleAt(wednesday, s_on, new FfaSettings { WeekendOnly = false }));
        Assert.Equal(new TestingGrounds.Schedule(true, false, true), TestingGrounds.ScheduleAt(saturday, s_on, new FfaSettings()));
        Assert.True(s_on.Enabled);
        Assert.True(s_on.TwoVsTwoEnabled);
    }

    [Fact]
    // Every schedule has its own Crc offset, under Arenas' 200,000 so no sum with Arenas' offsets meets another
    // (ArenaHissTests).
    public void EverySchedulesCrcOffsetIsItsOwn()
    {
        var offsets = (from a in new[] { false, true } from b in new[] { false, true } from c in new[] { false, true }
                       select new TestingGrounds.Schedule(a, b, c).CrcOffset).ToList();
        // A schedule with 2v2 open never shows FFA's tile, so those two pairs may share theirs.
        Assert.Equal(6, offsets.Distinct().Count());
        Assert.All(offsets, o => Assert.InRange(o, 0, 199_999));
        // FFA's tile alone stays 0, nothing at all 50,000 (Twosday), as before.
        Assert.Equal(0, new TestingGrounds.Schedule(false, false, true).CrcOffset);
        Assert.Equal(50_000, new TestingGrounds.Schedule(false, false, false).CrcOffset);
    }

    [Fact]
    public void TheClosedAnswerNamesTheQueuesDays()
    {
        Assert.Equal("1v1 Testing Grounds is open every day, except during Twosday.",
            (string?)TestingGrounds.ClosedFailure(TestingGrounds.OneVsOne, new FfaSettings())["body"]!["ErrorMessage"]);
        Assert.Equal(
            "2v2 Testing Grounds is open Monday, Wednesday, Friday and Sunday (US Eastern), except during Twosday; Free For All takes its place on Tuesday, Thursday and Saturday.",
            (string?)TestingGrounds.ClosedFailure(TestingGrounds.TwoVsTwo, new FfaSettings())["body"]!["ErrorMessage"]);
    }

    [Fact]
    public void ItsMatchesRunTheirQueuesMutatorsUnranked()
    {
        var config = TestingGrounds.ConfigOverride(s_on, TestingGrounds.OneVsOne);
        Assert.False(config["bIsCustomGame"]!.GetValue<bool>());
        // No XP and no missions (MissionService reads it), whatever RewardTracks:MatchXp says.
        Assert.False(config["bModeGrantsProgress"]!.GetValue<bool>());
        Assert.Equal("evtq_1v1testinggrounds", config["EventQueueSlug"]!.GetValue<string>());
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp"], config["WorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));

        var twos = TestingGrounds.ConfigOverride(s_on, TestingGrounds.TwoVsTwo);
        Assert.False(twos["bModeGrantsProgress"]!.GetValue<bool>());
        Assert.Equal("evtq_2v2testinggrounds", twos["EventQueueSlug"]!.GetValue<string>());
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp", "ovs_2v2_individual_stocks", "ovs_friendly_fire"],
            twos["WorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal("2v2", TestingGrounds.TwoVsTwo.Mode);
    }

    [Fact]
    // Tuggernuts, 2026-10-27: 6 lives each in 2v2 with Individual Stocks, so 12 ringouts to win; without it, and in 1v1, the
    // mode's own.
    public void TwoVsTwoWithIndividualStocksIsPlayedToTwiceItsLives()
    {
        Assert.Equal(6, s_on.TwoVsTwoLives);
        Assert.Equal(12, TestingGrounds.RingoutsOf(s_on, TestingGrounds.TwoVsTwo));
        Assert.Equal(8, TestingGrounds.RingoutsOf(new TestingGroundsSettings { TwoVsTwoLives = 4 }, TestingGrounds.TwoVsTwo));
        Assert.Null(TestingGrounds.RingoutsOf(s_on, TestingGrounds.OneVsOne));
        Assert.Null(TestingGrounds.RingoutsOf(new TestingGroundsSettings { TwoVsTwoMutators = "ovs_beta_speed" }, TestingGrounds.TwoVsTwo));
    }

    [Fact]
    public void ItsMutatorsComeFromTheSettings()
    {
        var settings = new TestingGroundsSettings { Mutators = " ovs_beta_speed , ,ovs_friendly_fire,ovs_beta_speed", TwoVsTwoMutators = "ovs_shield_hp" };
        Assert.Equal(["ovs_beta_speed", "ovs_friendly_fire"], TestingGrounds.MutatorsOf(settings));
        Assert.Equal(["ovs_shield_hp"], TestingGrounds.MutatorsOf(settings, TestingGrounds.TwoVsTwo));
        Assert.Empty(TestingGrounds.MutatorsOf(new TestingGroundsSettings { Mutators = "" }));
    }

    [Fact]
    public void WithBetaSpeedAPlayerMustRunTheServersSpeed()
    {
        var betaSpeed = new OpenVersus.Server.Core.Matches.BetaSpeedSettings { Percent = 120 };
        foreach (var queue in new[] { TestingGrounds.OneVsOne, TestingGrounds.TwoVsTwo })
        {
            Assert.False(TestingGrounds.WrongBetaSpeed(s_on, queue, betaSpeed, "120"));
            // Another speed (read before a change), or none (an older client).
            Assert.True(TestingGrounds.WrongBetaSpeed(s_on, queue, betaSpeed, "110"));
            Assert.True(TestingGrounds.WrongBetaSpeed(s_on, queue, betaSpeed, null));
        }

        // Without the mutator the speed does not matter.
        Assert.False(TestingGrounds.WrongBetaSpeed(new TestingGroundsSettings { Mutators = "ovs_shield_hp" }, betaSpeed, null));
        Assert.False(TestingGrounds.WrongBetaSpeed(new TestingGroundsSettings { TwoVsTwoMutators = "ovs_shield_hp" }, TestingGrounds.TwoVsTwo, betaSpeed, null));
        var failure = TestingGrounds.BetaSpeedFailure();
        Assert.Equal(1, failure["return_code"]!.GetValue<int>());
        Assert.Equal("BetaSpeedChanged", failure["body"]!["ErrorCode"]!.GetValue<string>());
    }

    [Fact]
    public void QueuesAreFoundByTheirListAndEventQueue()
    {
        Assert.Same(TestingGrounds.OneVsOne, TestingGrounds.ByList("testinggrounds1v1"));
        Assert.Same(TestingGrounds.TwoVsTwo, TestingGrounds.ByList("testinggrounds2v2"));
        Assert.Null(TestingGrounds.ByList("2v2"));
        Assert.Same(TestingGrounds.TwoVsTwo, TestingGrounds.ByEventQueue("evtq_2v2testinggrounds"));
        Assert.Null(TestingGrounds.ByEventQueue("evtq_ffa"));
        Assert.Equal(TestingGrounds.List, TestingGrounds.OneVsOne.List);
        Assert.Equal(TestingGrounds.Criteria, TestingGrounds.OneVsOne.Criteria);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void TheHissShowsTheOpenQueuesOnly(bool oneVsOne, bool twoVsTwo, bool ffaTile)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        TestingGrounds.ApplyTo(answer, new TestingGrounds.Schedule(oneVsOne, twoVsTwo, ffaTile), s_on);
        var data = answer["body"]!["Data"]!;
        var toggles = data["feature-toggles"]!["_hydra_compressed"]!;
        var queues = data["event-queue-config"]!["_hydra_compressed"]!;
        Assert.Equal(oneVsOne, toggles["evtq_1v1testinggrounds"]!.GetValue<bool>());
        Assert.Equal(twoVsTwo, toggles["evtq_2v2testinggrounds"]!.GetValue<bool>());
        Assert.Equal(ffaTile, toggles["FFA"]!.GetValue<bool>());
        Assert.Equal(ffaTile, toggles["evtq_ffa"]!.GetValue<bool>());
        Assert.Equal(ffaTile, queues["evtq_ffa"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        Assert.Equal(oneVsOne, queues["evtq_1v1testinggrounds"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        Assert.Equal(twoVsTwo, queues["evtq_2v2testinggrounds"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
        // 2v2's catalog entry takes parties, as the game has it.
        Assert.True(queues["evtq_2v2testinggrounds"]!["data"]!["bPartyEligible"]!.GetValue<bool>());
        var modes = data["game-mode-config"]!["_hydra_compressed"]!;
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp"],
            modes["gm_1v1shields"]!["data"]!["GameModeData"]!["RequiredWorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal(["ovs_beta_speed", "ovs_shield_hp", "ovs_2v2_individual_stocks", "ovs_friendly_fire"],
            modes["gm_2v2shields"]!["data"]!["GameModeData"]!["RequiredWorldBuffs"]!.AsArray().Select(b => b!.GetValue<string>()));
    }
}
