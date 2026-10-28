using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// Twosday's window, read as the TS server reads it (tests/twosdayWindow.test.ts, branch openversus, the same cases), its
/// settings, and Testing Grounds closed while it is on. Needs no stores.
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
        Assert.ThrowsAny<Exception>(() => Twosday.IsActive(new Dictionary<string, string> { ["day"] = "7" }, now));
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
    public void TheSwitchIsOnUnlessTurnedOff()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        Assert.True(Twosday.IsActive(new Dictionary<string, string>(), tuesdayEvening));
        Assert.True(Twosday.IsActive(new Dictionary<string, string> { ["enabled"] = "1" }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new Dictionary<string, string> { ["enabled"] = "off" }, tuesdayEvening));
        Assert.False(Twosday.IsActive(new Dictionary<string, string> { ["enabled"] = "0" }, tuesdayEvening));
        // Moved to Wednesday by the hash: Tuesday is then not Twosday.
        Assert.False(Twosday.IsActive(new Dictionary<string, string> { ["day"] = "3" }, tuesdayEvening));
    }

    [Fact]
    public void TestingGroundsClosesForTwosdayUnlessAlwaysOpen()
    {
        // On a 2v2 Testing Grounds day (FFA on the old weekend, so Tuesday is one).
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        var weekendFfa = new FfaSettings { Days = "Fri,Sat,Sun" };
        Assert.Equal(new TestingGrounds.Schedule(true, true, false), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), weekendFfa));
        Assert.Equal(new TestingGrounds.Schedule(false, false, false), TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings(), weekendFfa, twosday: true));
        Assert.Equal(new TestingGrounds.Schedule(true, true, false),
            TestingGrounds.ScheduleAt(tuesdayEvening, new TestingGroundsSettings { AlwaysOpen = true }, weekendFfa, twosday: true));
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
    public void TheHashsSettingsWithTheirDefaults()
    {
        var defaults = Twosday.SettingsOf(new Dictionary<string, string>());
        Assert.Equal(new Twosday.Settings(true, Twosday.Default, 30), defaults);
        var set = Twosday.SettingsOf(new Dictionary<string, string> { ["enabled"] = "false", ["day"] = "3", ["start"] = "18:00", ["breakup_minutes"] = "0" });
        Assert.Equal(new Twosday.Settings(false, Twosday.Default with { Day = DayOfWeek.Wednesday, Start = "18:00" }, 0), set);
        Assert.ThrowsAny<FormatException>(() => Twosday.SettingsOf(new Dictionary<string, string> { ["breakup_minutes"] = "-1" }));
        Assert.ThrowsAny<FormatException>(() => Twosday.SettingsOf(new Dictionary<string, string> { ["breakup_minutes"] = "soon" }));
    }

    [Fact]
    // Twosday:Enabled off (a test server): never Twosday, whatever the hash says, without reading Redis.
    public async Task TheDeploymentsSwitchTurnsItOffEverywhere()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        var off = new ServiceCollection().Configure<TwosdaySettings>(o => o.Enabled = false).BuildServiceProvider();
        Assert.False(Twosday.IsEnabled(off));
        Assert.False(await Twosday.IsActiveAsync(off, tuesdayEvening));
        Assert.True(Twosday.IsEnabled(new ServiceCollection().BuildServiceProvider()));
        Assert.True(new TwosdaySettings().Enabled);
    }
}
