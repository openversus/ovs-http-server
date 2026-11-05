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
    [InlineData("""[{"isBot":false},{"isBot":true}]""", true)]
    [InlineData("""[{"isBot":false},{"isBot":false},{"isSpectator":true}]""", true)]
    [InlineData("""[{"isBot":false},{"isBot":true},{"isBot":false},{"isBot":true}]""", true)]
    [InlineData("""[{},{},{},{},{"isSpectator":true},{"isSpectator":true},{"isSpectator":true},{"isSpectator":true}]""", true)]
    [InlineData("""[{}]""", true)]
    [InlineData("""[{"isBot":true},{"isBot":true}]""", false)]
    [InlineData("""[{"isSpectator":true},{"isBot":true}]""", false)]
    [InlineData("""[]""", false)]
    public void EligibleIsAnyMatchWithAHumanWhoPlays(string players, bool eligible) => Assert.Equal(eligible, P2P.IsEligible(JsonNode.Parse(players)!.AsArray()));

    [Fact]
    public void MarkAlwaysWritesP2P()
    {
        var notification = new JsonObject { ["players"] = JsonNode.Parse("""[{"playerId":"a"},{"playerId":"b"}]""") };
        Assert.False(P2P.Mark(notification, enabled: false, hasNode: _ => true));
        Assert.False(notification["p2p"]!.GetValue<bool>());
        Assert.True(P2P.Mark(notification, enabled: true, hasNode: _ => true));
        Assert.True(notification["p2p"]!.GetValue<bool>());
    }

    [Theory]
    // Every human, spectators included, must have a node: each is sent to their own (2026-10-19: a game with its node
    // off was sent to 127.0.0.1 with nothing there, and no relay could reach it). Bots need none.
    [InlineData("""[{"playerId":"a"},{"playerId":"b"}]""", "a,b", true)]
    [InlineData("""[{"playerId":"a"},{"playerId":"b"}]""", "a", false)]
    [InlineData("""[{"playerId":"a"},{"playerId":"b"}]""", "", false)]
    [InlineData("""[{"playerId":"a"},{"playerId":"bot","isBot":true}]""", "a", true)]
    [InlineData("""[{"playerId":"a"},{"playerId":"b"},{"playerId":"s","isSpectator":true}]""", "a,b", false)]
    [InlineData("""[{"playerId":"a"},{"playerId":"b"},{"playerId":"s","isSpectator":true}]""", "a,b,s", true)]
    // An entry without a playerId cannot be asked about: a server match.
    [InlineData("""[{"playerId":"a"},{}]""", "a", false)]
    public void P2PNeedsANodeForEveryHuman(string players, string withNode, bool p2p)
    {
        var nodes = withNode.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var notification = new JsonObject { ["players"] = JsonNode.Parse(players) };
        Assert.Equal(p2p, P2P.Mark(notification, enabled: true, hasNode: nodes.Contains));
        Assert.Equal(p2p, notification["p2p"]!.GetValue<bool>());
    }

    [Fact]
    // Switched off, or with nobody whose node could host, no node is asked about.
    public void NodesAreAskedAboutOnlyForAnEligibleMatchWithTheSwitchOn()
    {
        int asked = 0;
        bool HasNode(string _)
        {
            asked++;
            return true;
        }

        Assert.False(P2P.Mark(new JsonObject { ["players"] = JsonNode.Parse("""[{"playerId":"a"},{"playerId":"b"}]""") }, enabled: false, HasNode));
        Assert.False(P2P.Mark(new JsonObject { ["players"] = JsonNode.Parse("""[{"playerId":"s","isSpectator":true},{"isBot":true}]""") }, enabled: true, HasNode));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void HumansArePlayersAndSpectatorsButNotBots() =>
        Assert.Equal(["a", "s", ""], P2P.Humans(JsonNode.Parse("""[{"playerId":"a"},{"playerId":"b","isBot":true},{"playerId":"s","isSpectator":true},{}]""")!.AsArray()));
}
