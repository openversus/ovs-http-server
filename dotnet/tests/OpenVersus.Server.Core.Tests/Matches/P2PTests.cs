using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>The TS src/p2p.ts and services/nodePort.ts rules.</summary>
public sealed class P2PTests
{
    [Theory]
    [InlineData("51561", 51561)]
    [InlineData(" 41234 ", 41234)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("0", 0)]
    [InlineData("65536", 0)]
    [InlineData("70000", 0)]
    [InlineData("123456", 0)]
    [InlineData("-5", 0)]
    [InlineData("1e3", 0)]
    [InlineData("12.5", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    // Not ASCII digits: \d in a JavaScript regex without the u flag is [0-9].
    [InlineData("٣٤", 0)]
    public void ANodePortStringIsOneToFiveDigitsInRange(string? text, int port)
    {
        Assert.Equal(port, P2P.ParseNodePort(text));
        Assert.Equal(port, P2P.ParseNodePort(text is null ? null : JsonValue.Create(text)));
    }

    [Theory]
    [InlineData("51561", 51561)]
    [InlineData("12.5", 0)]
    [InlineData("0", 0)]
    [InlineData("65536", 0)]
    [InlineData("true", 0)]
    [InlineData("null", 0)]
    public void ANodePortClaimMayBeANumber(string json, int port) => Assert.Equal(port, P2P.ParseNodePort(JsonNode.Parse(json)));

    [Fact]
    public void ANumberCreatedInCodeReadsAsParsedOne() => Assert.Equal(41234, P2P.ParseNodePort(JsonValue.Create(41234)));

    [Theory]
    // The ranked matchmaker's entries carry no isBot at all.
    [InlineData("""[{"playerId":"a"},{"playerId":"b"}]""", true)]
    [InlineData("""[{"isBot":false},{"isBot":false}]""", true)]
    [InlineData("""[{"isBot":false},{"isBot":true}]""", false)]
    [InlineData("""[{"isBot":false},{"isBot":false},{"isSpectator":true}]""", false)]
    [InlineData("""[{"isBot":false},{"isBot":true},{"isBot":false},{"isBot":true}]""", true)]
    [InlineData("""[{},{},{}]""", false)]
    [InlineData("""[{}]""", false)]
    public void EligibleIsTwoHumansAndNoSpectator(string players, bool eligible) => Assert.Equal(eligible, P2P.IsEligible(JsonNode.Parse(players)!.AsArray()));

    [Fact]
    public void MarkAlwaysWritesP2P()
    {
        var notification = new JsonObject { ["players"] = JsonNode.Parse("""[{},{}]""") };
        Assert.False(P2P.Mark(notification, enabled: false));
        Assert.False(notification["p2p"]!.GetValue<bool>());
        Assert.True(P2P.Mark(notification, enabled: true));
        Assert.True(notification["p2p"]!.GetValue<bool>());
    }
}
