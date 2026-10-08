using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Identity;

namespace OpenVersus.Server.Core.Tests.Identity;

public sealed class IdentifyTokensTests
{
    private const string Secret = "identify-secret-0123456789abcdef0123456789";
    private const string GameSecret = "game-secret-0123456789abcdef0123456789abcd";
    private static readonly DateTimeOffset s_now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SignsForThirtyDaysAndVerifiesWithItsOwnSecret()
    {
        string token = IdentifyTokens.Sign(new JsonObject { ["installId"] = "0123456789abcdef0123456789abcdef", ["steamVerified"] = "1" }, Secret, s_now);

        var claims = IdentifyTokens.Verify(token, Secret, s_now.AddDays(29));
        Assert.NotNull(claims);
        Assert.Equal("0123456789abcdef0123456789abcdef", (string?)claims["installId"]);
        Assert.True(IdentifyTokens.SteamVerified(claims));
        Assert.Null(IdentifyTokens.Verify(token, Secret, s_now.AddDays(31)));
    }

    [Fact]
    public void NeverPassesAsAGameSessionTokenNorTheOtherWayRound()
    {
        string identify = IdentifyTokens.Sign(new JsonObject { ["steamId"] = "76561198000000091" }, Secret, s_now);
        string game = AccessTokens.Sign(new JsonObject { ["id"] = "0000000000000000000a0001" }, GameSecret, TimeSpan.FromHours(1), s_now);

        Assert.Throws<AccessTokenException>(() => AccessTokens.Verify(identify, GameSecret, s_now));
        Assert.Null(IdentifyTokens.Verify(game, Secret, s_now));
        Assert.Null(IdentifyTokens.Verify(identify, null, s_now));
        Assert.Null(IdentifyTokens.Verify(null, Secret, s_now));
        Assert.Null(IdentifyTokens.Verify("not.a.token", Secret, s_now));
    }

    [Theory]
    [InlineData("""{"steamVerified":"1"}""", true)]
    [InlineData("""{"steamVerified":""}""", false)]
    [InlineData("""{"steamVerified":1}""", false)]
    [InlineData("""{"steamVerified":true}""", false)]
    [InlineData("""{}""", false)]
    public void TheProofIsTheStringOne(string json, bool verified)
    {
        Assert.Equal(verified, IdentifyTokens.SteamVerified(JsonNode.Parse(json)!.AsObject()));
        Assert.False(IdentifyTokens.SteamVerified(null));
    }
}
