using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Epic;
using OpenVersus.Server.Identity.Epic;

namespace OpenVersus.Server.Identity.Tests.Epic;

/// <summary>
/// The verifier over a key pair of its own standing in for Epic's: the token checks OpenID Connect names (signature,
/// issuer, audience, exp and nbf, subject), and the key cache (fetch once, refetch on an unknown kid at most once a
/// minute, the last keys the floor). No live Epic token has been seen yet (2026-10-08): the shape is the discovery
/// document's, not a sample's.
/// </summary>
public sealed class EpicIdTokenVerifierTests
{
    private const string ClientId = "xyza0000000000000000000000000000";
    private const string Issuer = "https://api.epicgames.dev/epic/oauth/v2";
    private const string Account = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset s_now = new(2026, 10, 8, 20, 0, 0, TimeSpan.Zero);

    private readonly RSA _epic = RSA.Create(2048);
    private readonly FakeSource _source;
    private readonly EpicSettings _settings = new() { ClientId = ClientId };
    private readonly FakeTime _time = new(s_now);

    public EpicIdTokenVerifierTests()
    {
        _source = new FakeSource(Jwks(("k1", _epic, null)));
    }

    private EpicIdTokenVerifier Verifier => new(_source, new Options(_settings), _time, NullLogger<EpicIdTokenVerifier>.Instance);

    // Tokens

    [Fact]
    public async Task AnEpicTokenForTheGameIsVerified()
    {
        var check = await Verifier.CheckAsync(Token(_epic, "k1"), s_now);
        var ok = Assert.IsType<EpicTokenCheck.Verified>(check);
        Assert.Equal(Account, ok.AccountId);
        Assert.Equal(s_now + TimeSpan.FromHours(1), ok.Expires);
        Assert.Equal(1, _source.Fetches);
    }

    [Fact]
    public async Task TheSubjectIsNormalizedAndMustBeAnAccountId()
    {
        var ok = Assert.IsType<EpicTokenCheck.Verified>(await Verifier.CheckAsync(Token(_epic, "k1", sub: Account.ToUpperInvariant()), s_now));
        Assert.Equal(Account, ok.AccountId);
        Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", sub: "someone"), s_now));
        Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", sub: ""), s_now));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS512")]
    [InlineData("")]
    public async Task OnlyRs256IsEverTried(string alg)
    {
        // Signed with the right key and the wrong name for it (or a MAC under the modulus, the classic confusion): refused on the name alone.
        var refused = Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", alg: alg), s_now));
        Assert.Contains("alg", refused.Reason);
    }

    [Fact]
    public async Task ASignatureFromAnotherKeyIsRefused()
    {
        using var other = RSA.Create(2048);
        var refused = Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(other, "k1"), s_now));
        Assert.Equal("bad signature", refused.Reason);
    }

    [Fact]
    public async Task ATamperedPayloadIsRefused()
    {
        string token = Token(_epic, "k1");
        string[] parts = token.Split('.');
        var claims = (JsonObject)JsonNode.Parse(EpicIdToken.Base64Url(parts[1]))!;
        claims["sub"] = "fedcba9876543210fedcba9876543210";
        string tampered = parts[0] + "." + EpicIdToken.ToBase64Url(Encoding.UTF8.GetBytes(claims.ToJsonString())) + "." + parts[2];
        Assert.Equal("bad signature", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(tampered, s_now)).Reason);
    }

    [Fact]
    public async Task TheIssuerMustBeEpics()
    {
        var refused = Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", iss: "https://api.epicgames.dev/auth/v1/oauth"), s_now));
        Assert.StartsWith("issuer", refused.Reason);
    }

    [Fact]
    public async Task TheAudienceMustNameTheClientIdAsAStringOrInAnArray()
    {
        Assert.IsType<EpicTokenCheck.Verified>(await Verifier.CheckAsync(Token(_epic, "k1", aud: new JsonArray("other", ClientId)), s_now));
        Assert.StartsWith("audience", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", aud: "other"), s_now)).Reason);
        Assert.StartsWith("audience", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", aud: new JsonArray("other")), s_now)).Reason);
        Assert.StartsWith("audience", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", noAud: true), s_now)).Reason);
    }

    [Fact]
    public async Task ExpiryAndNotBeforeHoldWithinTheSkew()
    {
        var expiry = s_now + TimeSpan.FromHours(1);
        Assert.IsType<EpicTokenCheck.Verified>(await Verifier.CheckAsync(Token(_epic, "k1"), expiry + TimeSpan.FromSeconds(59)));
        Assert.StartsWith("expired", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1"), expiry + TimeSpan.FromSeconds(61))).Reason);
        Assert.Equal("no exp", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", noExp: true), s_now)).Reason);

        Assert.IsType<EpicTokenCheck.Verified>(await Verifier.CheckAsync(Token(_epic, "k1", nbf: s_now + TimeSpan.FromSeconds(30)), s_now));
        Assert.Equal("not valid yet", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(Token(_epic, "k1", nbf: s_now + TimeSpan.FromSeconds(90)), s_now)).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not.a.jwt.at.all")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0")]
    [InlineData("!!!.e30.c2ln")]
    public async Task AnythingThatIsNotAJwtIsRefused(string token)
    {
        Assert.Equal("not a JWT", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(token, s_now)).Reason);
    }

    [Fact]
    public async Task AnOversizedTokenIsRefusedBeforeParsing()
    {
        string token = Token(_epic, "k1") + new string('A', EpicIdToken.MaxLength);
        Assert.Equal("not a JWT", Assert.IsType<EpicTokenCheck.Refused>(await Verifier.CheckAsync(token, s_now)).Reason);
    }

    [Fact]
    public async Task WithoutAClientIdNothingIsJudged()
    {
        _settings.ClientId = "";
        Assert.IsType<EpicTokenCheck.Unavailable>(await Verifier.CheckAsync(Token(_epic, "k1"), s_now));
        Assert.Equal(0, _source.Fetches);
    }

    // Keys

    [Fact]
    public async Task KeysAreFetchedOnceAndServeEveryToken()
    {
        var verifier = Verifier;
        for (int i = 0; i < 5; i++)
        {
            Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(_epic, "k1"), s_now + TimeSpan.FromMinutes(i)));
        }

        Assert.Equal(1, _source.Fetches);
        Assert.Equal(1, verifier.KeyCount);
    }

    [Fact]
    public async Task AnUnknownKidFetchesAgainAtMostOnceAMinute()
    {
        var verifier = Verifier;
        Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(_epic, "k1"), s_now));

        // Epic rotated: the new key is not held, the refetch is rate-limited, so the first token under it is refused...
        using var rotated = RSA.Create(2048);
        _source.Document = Jwks(("k1", _epic, null), ("k2", rotated, null));
        var refused = Assert.IsType<EpicTokenCheck.Refused>(await verifier.CheckAsync(Token(rotated, "k2"), s_now + TimeSpan.FromSeconds(30)));
        Assert.Contains("k2", refused.Reason);
        Assert.Equal(1, _source.Fetches);

        // ...and a minute after the last fetch the keys are fetched again and it verifies.
        Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(rotated, "k2"), s_now + TimeSpan.FromSeconds(61)));
        Assert.Equal(2, _source.Fetches);
        Assert.Equal(2, verifier.KeyCount);

        // A kid Epic never had: one more fetch after the minimum, then refused for good.
        Assert.IsType<EpicTokenCheck.Refused>(await verifier.CheckAsync(Token(_epic, "k9"), s_now + TimeSpan.FromMinutes(3)));
        Assert.Equal(3, _source.Fetches);
        Assert.IsType<EpicTokenCheck.Refused>(await verifier.CheckAsync(Token(_epic, "k9"), s_now + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(10)));
        Assert.Equal(3, _source.Fetches);
    }

    [Fact]
    public async Task WithNoKeysEverFetchedATokenIsUnavailableNotRefused()
    {
        _source.Document = null;
        var verifier = Verifier;
        Assert.IsType<EpicTokenCheck.Unavailable>(await verifier.CheckAsync(Token(_epic, "k1"), s_now));
        Assert.IsType<EpicTokenCheck.Unavailable>(await verifier.CheckAsync(Token(_epic, "k1"), s_now + TimeSpan.FromSeconds(10)));
        Assert.Equal(1, _source.Fetches);

        // Epic is back: the next due fetch picks the keys up.
        _source.Document = Jwks(("k1", _epic, null));
        Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(_epic, "k1"), s_now + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task TheLastKeysServeOnWhenEpicCannotBeReached()
    {
        var verifier = Verifier;
        Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(_epic, "k1"), s_now));

        _source.Document = null;
        _source.Throw = true;
        var later = s_now + TimeSpan.FromMinutes(_settings.JwksRefreshMinutes + 1);
        Assert.IsType<EpicTokenCheck.Verified>(await verifier.CheckAsync(Token(_epic, "k1", exp: later + TimeSpan.FromHours(1)), later));
        Assert.Equal(2, _source.Fetches);
    }

    [Fact]
    public void AJwksDocumentYieldsItsRsaSigningKeysOnly()
    {
        using var other = RSA.Create(2048);
        string jwks = Jwks(("k1", _epic, null), ("enc", other, "enc"), ("ec", null, null), ("k3", other, null));
        var keys = EpicKeySet.Parse(jwks);
        Assert.NotNull(keys);
        Assert.Equal(["k1", "k3"], keys.Keys.Order());
        Assert.Null(EpicKeySet.Parse("{\"nokeys\":[]}"));
        Assert.Null(EpicKeySet.Parse("not json"));
        Assert.Empty(EpicKeySet.Parse("{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"bad\",\"n\":\"!!\",\"e\":\"AQAB\"}]}")!);
    }

    // Helpers

    private static string Token(RSA key, string kid, string alg = "RS256", string iss = Issuer, object? aud = null, string sub = Account, bool noAud = false, DateTimeOffset? exp = null, DateTimeOffset? nbf = null, bool noExp = false)
    {
        var header = new JsonObject { ["alg"] = alg, ["kid"] = kid, ["typ"] = "JWT" };
        var claims = new JsonObject { ["iss"] = iss, ["sub"] = sub, ["iat"] = s_now.ToUnixTimeSeconds() };
        if (aud is JsonArray array)
        {
            claims["aud"] = array;
        }
        else if (!noAud)
        {
            claims["aud"] = aud as string ?? ClientId;
        }

        if (!noExp)
        {
            claims["exp"] = (exp ?? s_now + TimeSpan.FromHours(1)).ToUnixTimeSeconds();
        }

        if (nbf is { } notBefore)
        {
            claims["nbf"] = notBefore.ToUnixTimeSeconds();
        }

        return Sign(header, claims, key);
    }

    private static string Sign(JsonObject header, JsonObject claims, RSA key)
    {
        string input = EpicIdToken.ToBase64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + EpicIdToken.ToBase64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return input + "." + EpicIdToken.ToBase64Url(signature);
    }

    private static string Jwks(params (string Kid, RSA? Key, string? Use)[] keys)
    {
        var array = new JsonArray();
        foreach (var (kid, key, use) in keys)
        {
            if (key is null)
            {
                array.Add(new JsonObject { ["kty"] = "EC", ["kid"] = kid, ["crv"] = "P-256", ["x"] = "AA", ["y"] = "AA" });
                continue;
            }

            var p = key.ExportParameters(false);
            var entry = new JsonObject { ["kty"] = "RSA", ["kid"] = kid, ["n"] = EpicIdToken.ToBase64Url(p.Modulus!), ["e"] = EpicIdToken.ToBase64Url(p.Exponent!) };
            if (use is not null)
            {
                entry["use"] = use;
            }

            array.Add(entry);
        }

        return new JsonObject { ["keys"] = array }.ToJsonString();
    }

    private sealed class FakeSource(string? document) : IEpicKeySource
    {
        public string? Document { get; set; } = document;
        public bool Throw { get; set; }
        public int Fetches { get; private set; }

        public Task<string?> FetchAsync(CancellationToken ct)
        {
            Fetches++;
            if (Throw)
            {
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(Document);
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Options(EpicSettings value) : IOptionsMonitor<EpicSettings>
    {
        public EpicSettings CurrentValue => value;
        public EpicSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<EpicSettings, string?> listener) => null;
    }
}
