using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Preferences;

namespace OpenVersus.Server.Core.Tests.Preferences;

/// <summary>Reading a GameplayPreferences value: the same rules as the TS server's (tests/gameplayPreferences.test.ts).</summary>
public sealed class GameplayPreferencesTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("\"0\"", 0)]
    [InlineData("448", 448)]
    [InlineData("\"448\"", 448)]
    [InlineData("991", 991)]
    [InlineData("-5", -5)]
    // A JSON number by its value, as JS reads it.
    [InlineData("1000.0", 1000)]
    [InlineData("1e3", 1000)]
    public void AValueIsKeptAsSent(string json, long expected)
    {
        Assert.Equal(expected, GameplayPreferences.Parse(JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\" 448\"")]
    [InlineData("\"448 \"")]
    [InlineData("\"1e3\"")]
    [InlineData("\"0x10\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"-\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[448]")]
    [InlineData("9007199254740992")]
    public void AnythingElseIsNoValue(string json)
    {
        Assert.Null(GameplayPreferences.Parse(JsonNode.Parse(json)));
        Assert.Null(GameplayPreferences.Parse((JsonNode?)null));
    }

    [Theory]
    [InlineData(null, 964)]
    [InlineData("", 964)]
    [InlineData("abc", 964)]
    [InlineData("0", 0)]
    [InlineData("448", 448)]
    public void TheValueToPlayWithIsTheStoredOneZeroIncluded(string? stored, long expected) => Assert.Equal(expected, GameplayPreferences.Of(stored));
}
