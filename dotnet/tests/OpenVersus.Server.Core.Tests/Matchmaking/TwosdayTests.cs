using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// Twosday's window, read as the TS server reads it (tests/twosdayWindow.test.ts, branch openversus, the same cases), and
/// 1v1 Testing Grounds closed while it is on. Needs no stores.
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
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-06T20:00:00Z");
        Assert.True(TestingGrounds.IsOpen(tuesdayEvening, enabled: true, ffaWeekendOnly: true));
        Assert.False(TestingGrounds.IsOpen(tuesdayEvening, enabled: true, ffaWeekendOnly: true, twosday: true));
        Assert.True(TestingGrounds.IsOpen(tuesdayEvening, enabled: true, ffaWeekendOnly: true, alwaysOpen: true, twosday: true));
    }
}
