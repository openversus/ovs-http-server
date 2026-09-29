using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>
/// AccessTokens against jwt-fixtures.json, which the TS server's own jsonwebtoken wrote (tools/access/gen_jwt_fixtures.mjs):
/// the same claims, secret and time sign to the same token, and every token is accepted or refused as there.
/// </summary>
public sealed class AccessTokensTests
{
    private static readonly JsonObject s_fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Access", "jwt-fixtures.json")))!.AsObject();
    private static readonly string s_secret = (string)s_fixtures["secret"]!;
    private static readonly DateTimeOffset s_iat = DateTimeOffset.FromUnixTimeSeconds((long)s_fixtures["iat"]!);

    public static TheoryData<string> Signed() => [.. s_fixtures["signed"]!.AsArray().Select(s => (string)s!["name"]!)];

    public static TheoryData<string> Checks() => [.. s_fixtures["checks"]!.AsArray().Select(c => (string)c!["name"]!)];

    [Theory]
    [MemberData(nameof(Signed))]
    public void SignsTheSameTokenAsJsonwebtoken(string name)
    {
        var fixture = s_fixtures["signed"]!.AsArray().Single(s => (string)s!["name"]! == name)!;
        var claims = s_fixtures["claims"]!.DeepClone().AsObject();
        if (name == "accented name")
        {
            claims["username"] = "Zoë Ñandú";
        }

        string token = AccessTokens.Sign(claims, s_secret, AccessTokens.Lifetime((string)fixture["ttl"]!), s_iat);
        Assert.Equal((string)fixture["token"]!, token);
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void AcceptsWhatJsonwebtokenAccepts(string name)
    {
        var check = s_fixtures["checks"]!.AsArray().Single(c => (string)c!["name"]! == name)!;
        var at = DateTimeOffset.FromUnixTimeSeconds((long)check["at"]!);
        bool accepted;
        try
        {
            AccessTokens.Verify((string)check["token"]!, s_secret, at);
            accepted = true;
        }
        catch (AccessTokenException)
        {
            accepted = false;
        }

        Assert.Equal((bool)check["accepted"]!, accepted);
    }

    [Fact]
    public void RefusesAnotherAlgorithmThatJsonwebtokenWouldAccept()
    {
        // Nothing signs HS512 tokens with this secret, so accepting one only widens what a forger can try.
        Assert.True((bool)s_fixtures["hs512"]!["acceptedByTs"]!);
        Assert.Throws<AccessTokenException>(() => AccessTokens.Verify((string)s_fixtures["hs512"]!["token"]!, s_secret, s_iat));
    }

    [Fact]
    public void ReadsTheClaimsBack()
    {
        var claims = AccessTokens.Verify(AccessTokens.Sign(s_fixtures["claims"]!.DeepClone().AsObject(), s_secret, TimeSpan.FromHours(24), s_iat), s_secret, s_iat);
        Assert.Equal("76561198000000001", (string)claims["steamId"]!);
        Assert.Equal(964, (int)claims["GameplayPreferences"]!);
        Assert.Equal(s_iat.ToUnixTimeSeconds() + 86400, (long)claims["exp"]!);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("86400", 86400)]
    [InlineData("24h", 86400)]
    [InlineData("2m", 120)]
    [InlineData("7d", 604800)]
    [InlineData("30s", 30)]
    public void ReadsLifetimesAsTheTsServerDoes(string ttl, int? seconds)
    {
        Assert.Equal(seconds, (int?)AccessTokens.Lifetime(ttl)?.TotalSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("24x")]
    [InlineData("1.5h")]
    public void RefusesWhatTheTsServerRefuses(string ttl)
    {
        Assert.Throws<FormatException>(() => AccessTokens.Lifetime(ttl));
    }
}
