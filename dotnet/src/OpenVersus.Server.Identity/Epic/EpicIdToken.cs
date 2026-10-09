using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Identity.Epic;

/// <summary>How an Epic account ID token was judged.</summary>
public abstract record EpicTokenCheck
{
    /// <summary>Epic signed it for the game's client id and it is in date: <paramref name="AccountId"/> is the token's subject.</summary>
    public sealed record Verified(string AccountId, JsonObject Claims, DateTimeOffset Expires) : EpicTokenCheck;

    /// <summary>Not a token to trust: the Epic id it names is a claim at most.</summary>
    public sealed record Refused(string Reason) : EpicTokenCheck;

    /// <summary>It could not be judged (no signing keys to judge it with): the Epic id stays a claim, nothing is refused.</summary>
    public sealed record Unavailable(string Reason) : EpicTokenCheck;
}

/// <summary>
/// An Epic account ID token, as Epic's OpenID discovery document describes it: a JWT signed RS256 with one of the keys at
/// its JWKS, issuer <c>https://api.epicgames.dev/epic/oauth/v2</c>, audience the game's client id, subject the Epic
/// account id. Only the three parts are read here; <see cref="Verify"/> judges them against a key and the settings.
/// </summary>
/// <param name="KeyId">The header's kid.</param>
/// <param name="Algorithm">The header's alg, whatever it says.</param>
/// <param name="Claims">The payload.</param>
/// <param name="SigningInput">The bytes the signature covers (header.payload as sent).</param>
/// <param name="Signature">The decoded signature.</param>
public sealed record EpicIdToken(string KeyId, string Algorithm, JsonObject Claims, byte[] SigningInput, byte[] Signature)
{
    /// <summary>Longer than this is not a token anyone issued (Epic's are well under 2 KB).</summary>
    public const int MaxLength = 8192;

    private const string RequiredAlgorithm = "RS256";

    /// <summary>The token's parts, or null when <paramref name="token"/> is not three base64url parts with JSON objects in the first two.</summary>
    public static EpicIdToken? Parse(string token)
    {
        if (token.Length == 0 || token.Length > MaxLength)
        {
            return null;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(Base64Url(parts[0])) is not JsonObject header || JsonNode.Parse(Base64Url(parts[1])) is not JsonObject claims)
            {
                return null;
            }

            return new EpicIdToken(Str(header["kid"]), Str(header["alg"]), claims, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url(parts[2]));
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Judges the token with <paramref name="key"/>, the key its kid names: the algorithm must be RS256 (nothing else is
    /// ever tried against an RSA key), the signature must verify, the issuer must be <paramref name="issuer"/>, the
    /// audience (a string or an array) must name <paramref name="clientId"/>, exp and nbf must hold within
    /// <paramref name="skew"/>, and the subject must be an Epic account id.
    /// </summary>
    public EpicTokenCheck Verify(RSAParameters key, string issuer, string clientId, DateTimeOffset now, TimeSpan skew)
    {
        if (Algorithm != RequiredAlgorithm)
        {
            return new EpicTokenCheck.Refused($"alg {(Algorithm.Length > 0 ? Algorithm : "missing")}, not {RequiredAlgorithm}");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(key);
        }
        catch (CryptographicException)
        {
            return new EpicTokenCheck.Refused("the key its kid names is not an RSA key");
        }

        if (!rsa.VerifyData(SigningInput, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return new EpicTokenCheck.Refused("bad signature");
        }

        if (Str(Claims["iss"]) != issuer)
        {
            return new EpicTokenCheck.Refused($"issuer {Dash(Str(Claims["iss"]))}");
        }

        if (!Audiences().Contains(clientId, StringComparer.Ordinal))
        {
            return new EpicTokenCheck.Refused($"audience {Dash(string.Join(",", Audiences()))}, not the game's client id");
        }

        if (Claims["exp"] is not JsonValue expValue || !expValue.TryGetValue(out long exp))
        {
            return new EpicTokenCheck.Refused("no exp");
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (now > expires + skew)
        {
            return new EpicTokenCheck.Refused($"expired at {expires:O}");
        }

        if (Claims["nbf"] is JsonValue nbfValue && nbfValue.TryGetValue(out long nbf) && now + skew < DateTimeOffset.FromUnixTimeSeconds(nbf))
        {
            return new EpicTokenCheck.Refused("not valid yet");
        }

        string accountId = IdentityRules.Normalize(IdentityKind.Epic, Claims["sub"]);
        if (accountId.Length == 0)
        {
            return new EpicTokenCheck.Refused($"subject {Dash(Str(Claims["sub"]))} is not an Epic account id");
        }

        return new EpicTokenCheck.Verified(accountId, Claims, expires);
    }

    private IEnumerable<string> Audiences()
    {
        switch (Claims["aud"])
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item?.GetValueKind() == JsonValueKind.String)
                    {
                        yield return item.GetValue<string>();
                    }
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return value.GetValue<string>();
                break;
        }
    }

    private static string Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    private static string Dash(string value) => value.Length > 0 ? value : "-";

    /// <summary>Base64url (RFC 7515: no padding, - and _) to bytes; throws <see cref="FormatException"/> on anything else.</summary>
    public static byte[] Base64Url(string text)
    {
        string base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    /// <summary>Bytes to base64url, as tokens and JWKS fields are written.</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The RSA signing keys of a JWKS document, by kid: <c>{"keys":[{kty:"RSA", kid, n, e, use?}]}</c>.</summary>
public static class EpicKeySet
{
    /// <summary>The usable keys in <paramref name="jwks"/> (RSA, for signing or unsaid, with n and e); null when it is not a JWKS document at all.</summary>
    public static Dictionary<string, RSAParameters>? Parse(string jwks)
    {
        JsonObject? document;
        try
        {
            document = JsonNode.Parse(jwks) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (document?["keys"] is not JsonArray keys)
        {
            return null;
        }

        var result = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        foreach (var node in keys)
        {
            if (node is not JsonObject key)
            {
                continue;
            }

            string kid = Str(key["kid"]), use = Str(key["use"]), n = Str(key["n"]), e = Str(key["e"]);
            if (Str(key["kty"]) != "RSA" || (use.Length > 0 && use != "sig") || kid.Length == 0 || n.Length == 0 || e.Length == 0)
            {
                continue;
            }

            try
            {
                result[kid] = new RSAParameters { Modulus = EpicIdToken.Base64Url(n), Exponent = EpicIdToken.Base64Url(e) };
            }
            catch (FormatException)
            {
                // A key nobody can use is left out; the others still serve.
            }
        }

        return result;
    }

    private static string Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
}
