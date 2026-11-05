using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// The FFA queue's days (Ffa:Days, default Tuesday, Thursday and Saturday since 2026-10-16), each the whole day US Eastern
/// in EDT and EST, closed during Twosday; Testing Grounds has the others (TestingGroundsTests).
/// </summary>
public sealed class FfaScheduleTests
{
    [Theory]
    // EDT (UTC-4): Tuesday 2026-10-06 00:00 is 04:00 UTC; it closes at Wednesday 00:00 (Wednesday 04:00 UTC).
    [InlineData("2026-10-06T03:59:59Z", false)]
    [InlineData("2026-10-06T04:00:00Z", true)]
    [InlineData("2026-10-07T03:59:59Z", true)]
    [InlineData("2026-10-07T04:00:00Z", false)]
    [InlineData("2026-10-08T18:00:00Z", true)]   // Thursday
    [InlineData("2026-10-10T18:00:00Z", true)]   // Saturday
    [InlineData("2026-10-11T18:00:00Z", false)]  // Sunday
    [InlineData("2026-10-09T18:00:00Z", false)]  // Friday
    // EST (UTC-5): an hour later in UTC; Tuesday 04:30 UTC is still Monday in New York.
    [InlineData("2026-12-08T04:30:00Z", false)]
    [InlineData("2026-12-08T05:00:00Z", true)]
    [InlineData("2026-12-09T04:59:59Z", true)]
    [InlineData("2026-12-09T05:00:00Z", false)]
    public void OpenOnTuesdayThursdayAndSaturdayEastern(string now, bool open) =>
        Assert.Equal(open, FfaSchedule.IsOpen(DateTimeOffset.Parse(now), new FfaSettings()));

    [Fact]
    public void TheDaysAreASetting()
    {
        // The old weekend, by full names or three letters, any case; a word that is no day is ignored.
        var weekend = new FfaSettings { Days = " friday, SAT ,Sun, someday" };
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday], FfaSchedule.DaysOf(weekend.Days));
        Assert.True(FfaSchedule.IsOpen(DateTimeOffset.Parse("2026-10-11T18:00:00Z"), weekend));
        Assert.False(FfaSchedule.IsOpen(DateTimeOffset.Parse("2026-10-06T18:00:00Z"), weekend));
        Assert.Empty(FfaSchedule.DaysOf(""));
        Assert.False(FfaSchedule.IsOpen(DateTimeOffset.Parse("2026-10-06T18:00:00Z"), new FfaSettings { Days = "" }));
    }

    [Fact]
    // Tuggernuts, 2026-10-16: on its day FFA closes for Twosday's hours, as Testing Grounds does.
    public void TwosdayClosesItOnItsDay()
    {
        var tuesdayEvening = DateTimeOffset.Parse("2026-10-07T00:00:00Z");   // Tuesday 20:00 EDT
        Assert.True(FfaSchedule.IsOpen(tuesdayEvening, new FfaSettings()));
        Assert.False(FfaSchedule.IsOpen(tuesdayEvening, new FfaSettings(), twosday: true));
        // Open all week (Ffa:WeekendOnly off): Twosday does not close it, as before.
        Assert.True(FfaSchedule.IsOpen(tuesdayEvening, new FfaSettings { WeekendOnly = false }, twosday: true));
    }

    [Fact]
    public void AlwaysOpenWhenNotWeekendOnly() =>
        Assert.True(FfaSchedule.IsOpen(DateTimeOffset.Parse("2026-10-07T18:00:00Z"), new FfaSettings { WeekendOnly = false }));

    [Fact]
    // ffaQueueClosedFailure's shape, naming the days.
    public void TheClosedAnswerNamesTheDays() => Assert.Equal(
        """{"body":{"error":"ffa_queue_closed","ErrorCode":"QueueClosed","ErrorMessage":"Free For All is open Tuesday, Thursday and Saturday, US Eastern, except during Twosday."},"metadata":null,"return_code":1}""",
        Js.Stringify(FfaSchedule.ClosedFailure(new FfaSettings())));

    [Fact]
    public void OnItsDaysByDefault()
    {
        Assert.True(new FfaSettings().WeekendOnly);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday], FfaSchedule.DaysOf(new FfaSettings().Days));
    }
}
