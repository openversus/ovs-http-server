using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// Twosday's window, read as the TS server read it (tests/twosdayWindow.test.ts, branch openversus, the same cases), and
/// its settings (ovsctl's, in place of the TS server's Redis hash). Needs no stores.
/// </summary>
public sealed class TwosdayTests
{
    private static bool At(string iso) => Twosday.IsInWindow(DateTimeOffset.Parse(iso), Twosday.Default);

    [Theory]
    // Tuesday 2026-10-06, EDT (UTC-4).
    [InlineData("2026-10-06T18:59:59Z", false)]
    [InlineData("2026-10-06T19:00:00Z", true)]
    [InlineData("2026-10-07T03:59:59Z", true)]
    [InlineData("2026-10-07T04:00:00Z", false)]
    // Tuesday 2026-12-01, EST (UTC-5).
    [InlineData("2026-12-01T19:30:00Z", false)]
    [InlineData("2026-12-01T20:00:00Z", true)]
    [InlineData("2026-12-02T04:59:59Z", true)]
    [InlineData("2026-12-02T05:00:00Z", false)]
    // Never on another day.
    [InlineData("2026-10-05T20:00:00Z", false)]
    [InlineData("2026-10-07T20:00:00Z", false)]
    public void TuesdayThreePmToMidnightEastern(string now, bool open) => Assert.Equal(open, At(now));

    [Theory]
    [InlineData("2026-10-10T08:29:59Z", false)]
    [InlineData("2026-10-10T08:30:00Z", true)]
    [InlineData("2026-10-10T09:00:29Z", true)]
    [InlineData("2026-10-10T09:00:30Z", false)]
    public void OtherHoursDayAndZone(string now, bool open) =>
        Assert.Equal(open, Twosday.IsInWindow(DateTimeOffset.Parse(now), new Twosday.Window(DayOfWeek.Saturday, "09:30", "10:00:30", "Europe/London")));

    [Fact]
    public void ASettingItCannotReadThrows()
    {
        var now = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        Assert.ThrowsAny<Exception>(() => Twosday.IsInWindow(now, Twosday.Default with { Start = "3pm" }));
        Assert.ThrowsAny<Exception>(() => Twosday.IsInWindow(now, Twosday.Default with { Start = "16:00", End = "15:00" }));
        Assert.ThrowsAny<Exception>(() => Twosday.IsInWindow(now, Twosday.Default with { TimeZone = "Eastern/Nowhere" }));
    }

    [Fact]
    public void TimesOfDay()
    {
        Assert.Equal(15 * 3600, Twosday.SecondsOfDay("15:00"));
        Assert.Equal(86400, Twosday.SecondsOfDay("24:00"));
        Assert.Equal(86399, Twosday.SecondsOfDay("23:59:59"));
        Assert.Null(Twosday.SecondsOfDay("24:01"));
        Assert.Null(Twosday.SecondsOfDay("12:60"));
    }

    [Fact]
    // The settings: on by default in the default window; off by the switch; moved by the day, the hours and the zone; a
    // window that cannot be read is off, not an error.
    public void TheSettingsSwitchAndMoveIt()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        Assert.True(Twosday.IsActive(new TwosdaySettings(), tuesdayEvening));
        Assert.False(Twosday.IsActive(new TwosdaySettings { Enabled = false }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new TwosdaySettings { Day = DayOfWeek.Wednesday }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new TwosdaySettings { Start = "17:00" }, tuesdayEvening));
        Assert.True(Twosday.IsActive(new TwosdaySettings { Start = "17:00" }, tuesdayEvening.AddHours(1)));
        Assert.False(Twosday.IsActive(new TwosdaySettings { End = "16:00" }, tuesdayEvening));
        Assert.True(Twosday.IsActive(new TwosdaySettings { TimeZone = "Europe/London" }, DateTimeOffset.Parse("2026-10-06T14:00:00Z")));
        Assert.False(Twosday.IsActive(new TwosdaySettings { Start = "3pm" }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new TwosdaySettings { Start = "16:00", End = "15:00" }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new TwosdaySettings { TimeZone = "Eastern/Nowhere" }, tuesdayEvening));
        Assert.Equal(new Twosday.Window(DayOfWeek.Tuesday, "15:00", "24:00", "America/New_York"), Twosday.Default);
        Assert.Equal(30, new TwosdaySettings().BreakupMinutes);
    }

    [Fact]
    // Twosday keeps both Testing Grounds queues open: on a 2v2 day as it was, and on FFA's day 2v2 opens in FFA's place
    // (the 1v1 searches go to it); the schedule's own cases are TestingGroundsTests.
    public void TestingGroundsStaysOpenForTwosday()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        var weekendFfa = new FfaSettings { Days = "Fri,Sat,Sun" };
        Assert.Equal(new TestingGrounds.Schedule(true, true, false), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), weekendFfa));
        Assert.Equal(new TestingGrounds.Schedule(true, true, false), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), weekendFfa, twosday: true));
        Assert.Equal(new TestingGrounds.Schedule(true, false, true), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), new FfaSettings()));
        Assert.Equal(new TestingGrounds.Schedule(true, true, false), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), new FfaSettings(), twosday: true));
    }

    [Theory]
    // How far into the window, for the party breakup's first minutes; null outside it.
    [InlineData("2026-10-06T19:00:00Z", 0)]
    [InlineData("2026-10-06T19:29:59Z", 1799)]
    [InlineData("2026-10-07T03:59:59Z", 32399)]
    [InlineData("2026-10-06T18:59:59Z", null)]
    [InlineData("2026-10-07T04:00:00Z", null)]
    public void SecondsIntoTheWindow(string now, int? seconds) =>
        Assert.Equal(seconds, Twosday.SecondsInto(DateTimeOffset.Parse(now), Twosday.Default));

    [Fact]
    // Twosday:Enabled off (a test server): never Twosday, whatever the window says; the service's settings are read live.
    public void TheDeploymentsSwitchTurnsItOffEverywhere()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        var off = new ServiceCollection().Configure<TwosdaySettings>(o => o.Enabled = false).BuildServiceProvider();
        Assert.False(Twosday.IsEnabled(off));
        Assert.False(Twosday.IsActive(off, tuesdayEvening));
        var on = new ServiceCollection().BuildServiceProvider();
        Assert.True(Twosday.IsEnabled(on));
        Assert.True(Twosday.IsActive(on, tuesdayEvening));
        Assert.True(new TwosdaySettings().Enabled);
    }
}
