using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Identity;

/// <summary>
/// The token /api/identify gives the OpenVersus client (the TS server signed it with the game's JWT_SECRET for 30 days):
/// the same JWT shape, signed with <see cref="AccessSettings.IdentifySecret"/>, so it never passes a game route's token
/// check and a game session token never passes as an identify. Its claims name identifiers, and the Steam id among
/// them only when a session ticket proved it (steamVerified "1").
/// </summary>
public static class IdentifyTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static string Sign(JsonObject claims, string secret, DateTimeOffset now) => AccessTokens.Sign(claims, secret, Lifetime, now);

    /// <summary>The claims of a token the identify secret signed and that has not expired; null for anything else (no secret, no token, another signer).</summary>
    public static JsonObject? Verify(string? token, string? secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        try
        {
            return AccessTokens.Verify(token, secret, now);
        }
        catch (AccessTokenException)
        {
            return null;
        }
    }

    /// <summary>Whether the claims say the Steam id was proved by a ticket (the string "1", as the record and the TS-era flags are written).</summary>
    public static bool SteamVerified(JsonObject? claims) => Flag(claims, "steamVerified");

    /// <summary>Whether the claims say the Epic id was proved by the game's Epic ID token (epicVerified "1").</summary>
    public static bool EpicVerified(JsonObject? claims) => Flag(claims, "epicVerified");

    private static bool Flag(JsonObject? claims, string name) =>
        claims?[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>() == "1";
}
