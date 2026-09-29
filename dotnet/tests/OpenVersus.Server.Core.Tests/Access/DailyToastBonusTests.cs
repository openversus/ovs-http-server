using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>The daily bonus boundary: 11:00 in Chicago, today's once passed, else yesterday's, through both DST changes.</summary>
public sealed class DailyToastBonusTests
{
    [Theory]
    // CDT (UTC-5): 11:00 local is 16:00 UTC.
    [InlineData("2026-09-29T16:00:00Z", "2026-09-29T16:00:00Z")]
    [InlineData("2026-09-29T15:59:59Z", "2026-09-28T16:00:00Z")]
    [InlineData("2026-09-30T04:30:00Z", "2026-09-29T16:00:00Z")]
    // CST (UTC-6): 11:00 local is 17:00 UTC.
    [InlineData("2026-12-01T17:00:00Z", "2026-12-01T17:00:00Z")]
    [InlineData("2026-12-01T16:59:59Z", "2026-11-30T17:00:00Z")]
    // Before 11:00 on the day DST ends, the boundary is the previous day's, still CDT; after it, that day's in CST.
    [InlineData("2026-11-01T12:00:00Z", "2026-10-31T16:00:00Z")]
    [InlineData("2026-11-01T17:30:00Z", "2026-11-01T17:00:00Z")]
    // The day DST starts: 11:00 is already CDT.
    [InlineData("2026-03-08T16:30:00Z", "2026-03-08T16:00:00Z")]
    public void BoundaryIsTheMostRecentElevenInChicago(string now, string boundary)
    {
        Assert.Equal(DateTimeOffset.Parse(boundary).ToUnixTimeSeconds(), DailyToastBonus.MostRecentBoundary(DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void MatchesTheBoundaryTheTsServerWrote()
    {
        // lastToastBonusUnix after a login at 2026-09-29T05:47Z on the TS server (tools/access/access_diff.mjs).
        Assert.Equal(1790611200, DailyToastBonus.MostRecentBoundary(DateTimeOffset.FromUnixTimeSeconds(1790660835)));
    }
}
