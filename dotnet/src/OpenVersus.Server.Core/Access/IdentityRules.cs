using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Access;

/// <summary>Which id <see cref="IdentityRules.Normalize(IdentityKind, string?)"/> checks.</summary>
public enum IdentityKind
{
    Steam,
    Epic,
    Install,
    Hardware,
}

/// <summary>A hardware fingerprint, kept only when it is an explicit strong version-2 one.</summary>
public readonly record struct HardwareSignal(string HardwareId, string HardwareIdVersion, string HardwareIdQuality)
{
    public static readonly HardwareSignal None = new("", "", "");
}

/// <summary>
/// The TS server's identity rules (services/identityNormalization.ts), unchanged: which ids count, and which existing
/// account an identified or identity-less login may take over. The Mongo filters are the TS ones, clause for clause.
/// </summary>
public static partial class IdentityRules
{
    /// <summary>After this many days without a login, an account with a durable id loses its IP link to an identified login.</summary>
    public const int StaleIpLinkDays = 7;

    // JavaScript's \d is ASCII only; .NET's is any Unicode digit.
    [GeneratedRegex("^(unknown|null|none|n/a)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[0-9]{15,20}$")]
    private static partial Regex SteamId();

    [GeneratedRegex("^[a-fA-F0-9]{32}$")]
    private static partial Regex Hex32();

    [GeneratedRegex("^[a-fA-F0-9]{64}$")]
    private static partial Regex Hex64();

    /// <summary>The id if it is a real one of its kind (Epic, install and hardware ids lowercased), else "".</summary>
    public static string Normalize(IdentityKind kind, string? value)
    {
        if (value is null)
        {
            return "";
        }

        string candidate = Js.Trim(value);
        if (candidate.Length == 0 || Placeholder().IsMatch(candidate))
        {
            return "";
        }

        return kind switch
        {
            IdentityKind.Steam => SteamId().IsMatch(candidate) ? candidate : "",
            IdentityKind.Epic or IdentityKind.Install => Hex32().IsMatch(candidate) ? candidate.ToLowerInvariant() : "",
            _ => Hex64().IsMatch(candidate) ? candidate.ToLowerInvariant() : "",
        };
    }

    /// <summary><see cref="Normalize(IdentityKind, string?)"/> for a JSON value: anything but a string is no id.</summary>
    public static string Normalize(IdentityKind kind, JsonNode? value) =>
        Normalize(kind, value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null);

    /// <summary>The fingerprint when it is a strong version-2 one, else none. Hardware never picks an account.</summary>
    public static HardwareSignal NormalizeHardware(string? id, string? version, string? quality)
    {
        string hardwareId = Normalize(IdentityKind.Hardware, id);
        string hardwareIdVersion = Js.Trim(version ?? "");
        string hardwareIdQuality = Js.Trim(quality ?? "").ToLowerInvariant();
        return hardwareId.Length == 0 || hardwareIdVersion != "2" || hardwareIdQuality != "strong"
            ? HardwareSignal.None
            : new HardwareSignal(hardwareId, hardwareIdVersion, hardwareIdQuality);
    }

    /// <summary>True when the account carries a Steam, Epic or install id.</summary>
    public static bool HasDurableIdentity(BsonDocument account) =>
        Normalize(IdentityKind.Steam, Str(account, "steamId")).Length > 0
        || Normalize(IdentityKind.Epic, Str(account, "epicId")).Length > 0
        || Normalize(IdentityKind.Install, Str(account, "installId")).Length > 0;

    /// <summary>Mongo clauses matching an account that carries a durable id.</summary>
    public static BsonArray DurableIdClauses() =>
    [
        new BsonDocument("steamId", new BsonRegularExpression(@"^\d{15,20}$")),
        new BsonDocument("epicId", new BsonRegularExpression(@"^[a-f\d]{32}$", "i")),
        new BsonDocument("installId", new BsonRegularExpression(@"^[a-f\d]{32}$", "i")),
    ];

    /// <summary>The accounts on this IP that have no durable id (provisional ones, or the others).</summary>
    public static BsonDocument IdLessAccountFilter(string ip, bool provisional) => new()
    {
        { "ip", ip },
        { "provisional", provisional ? true : new BsonDocument("$ne", true) },
        { "$nor", DurableIdClauses() },
    };

    /// <summary>
    /// The IP rule: when an identified player logs in from an IP, every other account on it that carries a durable id
    /// and has not been seen for <see cref="StaleIpLinkDays"/> loses its IP link (it stays reachable by its id).
    /// </summary>
    public static BsonDocument StaleIpLinkFilter(string ip, ObjectId keepAccountId, DateTime now) => new()
    {
        { "ip", ip },
        { "_id", new BsonDocument("$ne", keepAccountId) },
        { "lastSeenAt", new BsonDocument("$lt", now.AddDays(-StaleIpLinkDays)) },
        { "provisional", new BsonDocument("$ne", true) },
        { "$or", DurableIdClauses() },
    };

    /// <summary>
    /// Install-id adoption: a newly identified client that matches no account takes over the IP's single
    /// non-provisional account without a durable id, else the IP's most recently seen provisional one. Accounts with a
    /// durable id are never taken; two or more id-less ones (a household) are ambiguous.
    /// </summary>
    public static BsonDocument? ChooseAdoptionCandidate(IEnumerable<BsonDocument> candidates)
    {
        var idLess = candidates.Where(c => !HasDurableIdentity(c)).ToList();
        var legacy = idLess.Where(c => !IsProvisional(c)).ToList();
        if (legacy.Count == 1)
        {
            return legacy[0];
        }

        if (legacy.Count > 1)
        {
            return null;
        }

        // Stable, as JavaScript's sort is: equal dates keep their order.
        return idLess.Where(IsProvisional).OrderByDescending(LastSeen).FirstOrDefault();
    }

    /// <summary>
    /// An identity-less login recovers an IP's account only when the IP has one defensible owner: an account with a
    /// durable id outranks id-less ones, two of them are never guessed between, and provisional accounts never count.
    /// </summary>
    public static BsonDocument? ChooseUnambiguousLegacyIpCandidate(IEnumerable<BsonDocument> candidates)
    {
        var real = candidates.Where(c => !IsProvisional(c)).ToList();
        var canonical = real.Where(HasDurableIdentity).ToList();
        if (canonical.Count == 1)
        {
            return canonical[0];
        }

        if (canonical.Count > 1)
        {
            return null;
        }

        return real.Count == 1 ? real[0] : null;
    }

    // provisional === true: only a real true counts.
    private static bool IsProvisional(BsonDocument account) =>
        account.TryGetValue("provisional", out var p) && p.IsBoolean && p.AsBoolean;

    private static DateTime LastSeen(BsonDocument account) =>
        account.TryGetValue("lastSeenAt", out var d) && d.IsValidDateTime ? d.ToUniversalTime() : DateTime.UnixEpoch;

    private static string? Str(BsonDocument account, string field) =>
        account.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;
}
