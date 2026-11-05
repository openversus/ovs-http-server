using OpenVersus.Server.Core.Leaderboards;

namespace OpenVersus.Server.Core.Tests.Leaderboards;

/// <summary>How the TS server reads a board's slug and count (parseLeaderboardSlug, parseInt).</summary>
public sealed class LeaderboardParsingTests
{
    [Theory]
    [InlineData("ranked_season5_1v1_all", "1v1", null)]
    [InlineData("ranked_season5_2v2_all", "2v2", null)]
    [InlineData("ranked_season5_1v1_character_shaggy", "1v1", "character_shaggy")]
    [InlineData("ranked_season5_2v2_character_wonder_woman", "2v2", "character_wonder_woman")]
    [InlineData("something_2v2ish", "2v2", null)]
    [InlineData("anything", "1v1", null)]
    public void Slugs(string slug, string mode, string? character) =>
        Assert.Equal((mode, character), LeaderboardService.ParseSlug(slug));

    [Theory]
    [InlineData("100", 100)]
    [InlineData("  7x", 7)]
    [InlineData("+5", 5)]
    [InlineData("-3", -3)]
    [InlineData("5,9", 5)]
    [InlineData("1e3", 1)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CountsAsParseIntReadsThem(string? text, int? expected) =>
        Assert.Equal(expected, LeaderboardService.JsParseInt(text));
}
