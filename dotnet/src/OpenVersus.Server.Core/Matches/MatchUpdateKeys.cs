using System.Security.Cryptography;
using System.Text;

namespace OpenVersus.Server.Core.Matches;

// The MatchUpdateKey header a rollback server's calls carry (Rollback:MatchUpdateKey, MATCHUPDATEKEY; the rollback
// server's Server__MatchUpdateKey), checked as the TS isValidMatchUpdateKey: lower-cased, the same length, in constant
// time. Unlike there, an unset key or the TS placeholder never matches, even the same placeholder sent by a rollback
// server that has no key either.
internal static class MatchUpdateKeys
{
    public const string Placeholder = "MisconfiguredMatchUpdateKey";

    /// <summary>Whether <paramref name="configured"/> is a key at all (set, and not the placeholder).</summary>
    public static bool IsConfigured(string configured) => configured.Length > 0 && configured != Placeholder;

    /// <summary>Null when <paramref name="provided"/> is the configured key; otherwise why not: "not configured" (this
    /// server has no key), "missing" or "invalid".</summary>
    public static string? Refusal(string? provided, string configured)
    {
        if (!IsConfigured(configured))
        {
            return "not configured";
        }

        if (string.IsNullOrEmpty(provided))
        {
            return "missing";
        }

        byte[] a = Encoding.UTF8.GetBytes(provided.ToLowerInvariant());
        byte[] b = Encoding.UTF8.GetBytes(configured.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? null : "invalid";
    }
}
