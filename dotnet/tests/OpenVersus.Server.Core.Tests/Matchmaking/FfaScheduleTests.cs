using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>The FFA queue's window: Friday 00:00 to Sunday 23:59 US Eastern, in EDT and EST, as the TS isFfaQueueOpen.</summary>
public sealed class FfaScheduleTests
{
    [Theory]
    // EDT (UTC-4): Friday 00:00 is 04:00 UTC; Sunday 23:59 (already closed) is Monday 03:59 UTC.
    [InlineData("2026-10-02T03:59:59Z", false)]
    [InlineData("2026-10-02T04:00:00Z", true)]
    [InlineData("2026-10-03T15:00:00Z", true)]
    [InlineData("2026-10-05T03:58:59Z", true)]
    [InlineData("2026-10-05T03:59:00Z", false)]
    [InlineData("2026-10-07T18:00:00Z", false)]
    // EST (UTC-5): an hour later in UTC; Friday 04:30 UTC is still Thursday in New York.
    [InlineData("2026-12-04T04:30:00Z", false)]
    [InlineData("2026-12-04T05:00:00Z", true)]
    [InlineData("2026-12-07T04:58:00Z", true)]
    [InlineData("2026-12-07T04:59:00Z", false)]
    // The Sunday DST ends: open all day.
    [InlineData("2026-11-01T05:30:00Z", true)]
    [InlineData("2026-11-02T04:58:00Z", true)]
    public void OpenFromFridayToSundayEastern(string now, bool open) =>
        Assert.Equal(open, FfaSchedule.IsOpen(DateTimeOffset.Parse(now), weekendOnly: true));

    [Fact]
    public void AlwaysOpenWhenNotWeekendOnly() =>
        Assert.True(FfaSchedule.IsOpen(DateTimeOffset.Parse("2026-10-07T18:00:00Z"), weekendOnly: false));

    [Fact]
    // ffaQueueClosedFailure, as JSON.stringify writes it.
    public void TheClosedAnswerIsTheTsServers() => Assert.Equal(
        """{"body":{"error":"ffa_queue_closed","ErrorCode":"QueueClosed","ErrorMessage":"Free For All is open Friday 12:00am to Sunday 11:59pm Eastern."},"metadata":null,"return_code":1}""",
        Js.Stringify(FfaSchedule.ClosedFailure()));

    [Fact]
    public void WeekendOnlyByDefault() => Assert.True(new FfaSettings().WeekendOnly);
}
