using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Access;

/// <summary>Game session tokens. JWT_SECRET and ACCESS_TOKEN_TTL, the TS server's names, fill these.</summary>
public sealed class AccessSettings
{
    [Description("Signs and checks every game session token. Every service that reads tokens (the TS ones too, until they are ported) must share it. At least 32 characters.")]
    [Secret]
    [MinLength(32)]
    public string? JwtSecret { get; set; }

    [Description("How long a game session token lasts: seconds (86400), or a number with s, m, h or d (24h, 7d). Empty: tokens never expire.")]
    [RegularExpression("^$|^[1-9][0-9]*[smhd]?$")]
    public string TokenTtl { get; set; } = "24h";

    [Description("Game routes answer a request whose Host is Realtime:Domain (WB_DOMAIN) without a session token, as the TS server always does. A testing aid with its own verification elsewhere; off, every game route needs a token.")]
    public bool SkipTokenCheckForDomainHost { get; set; }

    [Description("The avatar URL in the login response's account identity.")]
    public string IdentityAvatarUrl { get; set; } = "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png";

    [Description("The Steam avatar URL in the login response's linked Steam account.")]
    public string SteamAvatarUrl { get; set; } = "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg";
}

/// <summary>Why a token was not accepted.</summary>
public sealed class AccessTokenException(string message) : Exception(message);

/// <summary>
/// JSON Web Tokens as the TS server's jsonwebtoken writes and checks them: HS256, the claims in the order given, then
/// <c>iat</c> and (with a lifetime) <c>exp</c>. The TS websocket server checks tokens signed here and the other way round,
/// so both must hold: same secret, same algorithm, same claim names and types. Only the bytes of the claims' JSON may
/// differ (escaping of unusual characters); a token is checked against its own bytes.
/// </summary>
public static class AccessTokens
{
    private static readonly byte[] s_header = Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}""");
    private static readonly JsonWriterOptions s_writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The lifetime ACCESS_TOKEN_TTL names, or null for none: a bare number is seconds (the TS server passes it to
    /// jsonwebtoken as a number), a number with s, m, h or d is read as the ms library reads it.
    /// </summary>
    public static TimeSpan? Lifetime(string? ttl)
    {
        if (string.IsNullOrEmpty(ttl))
        {
            return null;
        }

        var match = Regex.Match(ttl, "^([1-9][0-9]*)([smhd]?)$");
        if (!match.Success)
        {
            throw new FormatException($"ACCESS_TOKEN_TTL \"{ttl}\" is not a lifetime");
        }

        long n = long.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "" or "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromDays(n),
        };
    }

    /// <summary>Signs <paramref name="claims"/> (written in their order), adding iat and, with a lifetime, exp.</summary>
    public static string Sign(JsonObject claims, string secret, TimeSpan? lifetime, DateTimeOffset now)
    {
        long iat = now.ToUnixTimeSeconds();
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload, s_writer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in claims)
            {
                if (name is "iat" or "exp")
                {
                    continue;
                }

                writer.WritePropertyName(name);
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            writer.WriteNumber("iat", iat);
            if (lifetime is { } life)
            {
                // jsonwebtoken: Math.floor(iat + milliseconds / 1000).
                writer.WriteNumber("exp", iat + (long)Math.Floor(life.TotalSeconds));
            }

            writer.WriteEndObject();
        }

        string signingInput = $"{Base64Url(s_header)}.{Base64Url(payload.ToArray())}";
        return $"{signingInput}.{Base64Url(Mac(secret, signingInput))}";
    }

    /// <summary>
    /// The claims of a token this secret signed, as jsonwebtoken's verify accepts it: HS256 only, a matching signature,
    /// and not expired (exp) or not yet valid (nbf). Throws <see cref="AccessTokenException"/> otherwise.
    /// </summary>
    public static JsonObject Verify(string token, string secret, DateTimeOffset now)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new AccessTokenException("jwt malformed");
        }

        JsonObject header = ParseObject(parts[0], "header");
        if (header["alg"]?.GetValueKind() != JsonValueKind.String || (string?)header["alg"] != "HS256")
        {
            throw new AccessTokenException("invalid algorithm");
        }

        byte[] signature;
        try
        {
            signature = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            throw new AccessTokenException("invalid signature");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Mac(secret, $"{parts[0]}.{parts[1]}")))
        {
            throw new AccessTokenException("invalid signature");
        }

        JsonObject claims = ParseObject(parts[1], "payload");
        long seconds = now.ToUnixTimeSeconds();
        if (claims["nbf"] is { } nbf && (nbf.GetValueKind() != JsonValueKind.Number || seconds < nbf.GetValue<double>()))
        {
            throw new AccessTokenException("jwt not active");
        }

        if (claims["exp"] is { } exp && (exp.GetValueKind() != JsonValueKind.Number || seconds >= exp.GetValue<double>()))
        {
            throw new AccessTokenException("jwt expired");
        }

        return claims;
    }

    private static JsonObject ParseObject(string part, string what)
    {
        try
        {
            return JsonNode.Parse(FromBase64Url(part)) as JsonObject ?? throw new AccessTokenException($"invalid {what}");
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new AccessTokenException($"invalid {what}");
        }
    }

    private static byte[] Mac(string secret, string signingInput) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(signingInput));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        string b64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }
}
