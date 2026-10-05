using System.Text.RegularExpressions;
using OpenVersus.Server.Core.Profiles;

namespace OpenVersus.Server.Core.Tests.Profiles;

/// <summary>A searched name is matched as the text it is: every pattern character is escaped.</summary>
public sealed class SearchPatternTests
{
    [Theory]
    [InlineData("a.b", @"a\.b")]
    [InlineData("x+(y)", @"x\+\(y\)")]
    [InlineData(@"\d", @"\\d")]
    [InlineData("[a]{2}", @"\[a\]\{2\}")]
    [InlineData("^$|?*", @"\^\$\|\?\*")]
    [InlineData("Player_01", "Player_01")]
    [InlineData("♡Miku♡", "♡Miku♡")]
    public void Escapes(string text, string expected) => Assert.Equal(expected, ProfilesService.PcreEscape(text));

    [Theory]
    [InlineData("a.b", "a.b", true)]
    [InlineData("a.b", "axb", false)]
    [InlineData("x+", "xx", false)]
    [InlineData("x+", "the x+ player", true)]
    [InlineData("(", "(sad)", true)]
    [InlineData(@"\", @"back\slash", true)]
    // .NET's engine reads these escapes as PCRE does, so the pattern matches exactly its own text.
    public void MatchesOnlyTheText(string text, string name, bool matches) =>
        Assert.Equal(matches, Regex.IsMatch(name, ProfilesService.PcreEscape(text), RegexOptions.IgnoreCase));
}
