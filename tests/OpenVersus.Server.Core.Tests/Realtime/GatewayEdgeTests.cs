using OpenVersus.Server.Core.Realtime;

namespace OpenVersus.Server.Core.Tests.Realtime;

/// <summary>The edge's link codec (GatewayEdge, StreamId), which a node and an edge both use.</summary>
public sealed class GatewayEdgeTests
{
    [Fact]
    public void EachKindOfFrameReadsBackAsWritten()
    {
        var id = new StreamId(1_791_386_825_091, 3);
        byte[] frame = [0x09, 0x01, 0x00];

        var unlogged = GatewayEdge.Read(GatewayEdge.Unlogged(frame));
        Assert.Equal(GatewayEdge.Kind.Unlogged, unlogged.Kind);
        Assert.Equal(frame, unlogged.Frame);
        var logged = GatewayEdge.Read(GatewayEdge.Logged(id, frame));
        Assert.Equal((GatewayEdge.Kind.Logged, id), (logged.Kind, logged.Id));
        Assert.Equal(frame, logged.Frame);
        var close = GatewayEdge.Read(GatewayEdge.Close(4001, "é bye"));
        Assert.Equal((GatewayEdge.Kind.Close, 4001, "é bye"), (close.Kind, close.Code, close.Reason));
        Assert.Equal(0, GatewayEdge.Read(GatewayEdge.Close(0, null)).Code);
        var position = GatewayEdge.Read(GatewayEdge.Position(id));
        Assert.Equal((GatewayEdge.Kind.Position, id), (position.Kind, position.Id));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0, 0 })]
    [InlineData(new byte[] { 2, 0 })]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 9, 0 })]
    public void AFrameThatIsNoneIsRefused(byte[] bytes) => Assert.Throws<FormatException>(() => GatewayEdge.Read(bytes));

    [Fact]
    public void TheSecretMustBeSetAndMatch()
    {
        Assert.True(GatewayEdge.SecretMatches("s3cret", "s3cret"));
        Assert.False(GatewayEdge.SecretMatches("s3cret", "s3creT"));
        Assert.False(GatewayEdge.SecretMatches("", ""));
        Assert.False(GatewayEdge.SecretMatches("anything", null));
        Assert.False(GatewayEdge.SecretMatches(null, "s3cret"));
    }

    [Theory]
    [InlineData("0f3c9d2a4b5e4f6a8b9c0d1e2f3a4b5c", true)]
    [InlineData("edge-conn_1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("quote\"", false)]
    public void AConnectionIdIsShortAndPlain(string id, bool ok)
    {
        Assert.Equal(ok, GatewayEdge.IsConnectionId(id));
        Assert.False(GatewayEdge.IsConnectionId(new string('a', GatewayEdge.MaxConnectionIdLength + 1)));
    }

    [Fact]
    public void StreamIdsParseAndCompareAsMsThenSeq()
    {
        Assert.True(StreamId.TryParse("1791386825091-12", out var a));
        Assert.Equal(new StreamId(1_791_386_825_091, 12), a);
        Assert.Equal("1791386825091-12", a.ToString());
        Assert.True(new StreamId(5, 0) > new StreamId(4, 99));
        Assert.True(new StreamId(5, 2) > new StreamId(5, 1));
        Assert.True(StreamId.Zero < a);
        foreach (string bad in new[] { "", "12", "-1", "1-", "a-1", "1-2-3", " 1-2" })
        {
            Assert.False(StreamId.TryParse(bad, out _), bad);
        }
    }
}
