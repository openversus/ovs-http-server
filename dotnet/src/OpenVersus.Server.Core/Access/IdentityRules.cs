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

    /// <summary>
    /// Whether the account holding a login's install id is that login's person (2026-10-14: players pass their plugin
    /// folder, install id included, to friends, whose first launch then signed into the sender's account). A login that
    /// proves no Steam or Epic id is its install, as before. One that does is the install's account's person when the
    /// account holds the same Steam or Epic id; never when it holds a different one proved (a ticket or an Epic token: two
    /// people on one install); and otherwise, a different one only claimed or none of the login's ids, only from the
    /// same machine: both strong fingerprints equal for a claimed id (a claim is someone else's unless the machine says
    /// otherwise), not two different strong fingerprints for an account without the login's ids (an older install-only
    /// account takes its platform id, unless another machine has the copy).
    /// </summary>
    /// <param name="steamId">The login's proved Steam id, or "".</param>
    /// <param name="epicId">The login's Epic id (proved, or a claim while Epic ids are not checked), or "".</param>
    public static bool InstallOwnerIsThisPerson(BsonDocument owner, string steamId, string epicId, HardwareSignal hardware)
    {
        if (steamId.Length == 0 && epicId.Length == 0)
        {
            return true;
        }

        string ownSteam = Normalize(IdentityKind.Steam, Str(owner, "steamId"));
        string ownEpic = Normalize(IdentityKind.Epic, Str(owner, "epicId"));
        if ((steamId.Length > 0 && ownSteam == steamId) || (epicId.Length > 0 && ownEpic == epicId))
        {
            return true;
        }

        bool otherSteam = steamId.Length > 0 && ownSteam.Length > 0;
        bool otherEpic = epicId.Length > 0 && ownEpic.Length > 0;
        if ((otherSteam && SteamProved(owner, ownSteam)) || (otherEpic && EpicProved(owner)))
        {
            return false;
        }

        var stored = NormalizeHardware(Str(owner, "hardwareId"), Str(owner, "hardwareIdVersion"), Str(owner, "hardwareIdQuality"));
        bool compared = stored.HardwareId.Length > 0 && hardware.HardwareId.Length > 0;
        return otherSteam || otherEpic
            ? compared && stored.HardwareId == hardware.HardwareId
            : !compared || stored.HardwareId == hardware.HardwareId;
    }

    // The account's Steam id was proved: its last verified ticket names it (as AccessService reads it).
    private static bool SteamProved(BsonDocument account, string steamId) =>
        account.TryGetValue(Identity.IdentityRecord.TicketField, out var ticket) && ticket is BsonDocument doc && doc.GetValue("steam_id", "") == steamId;

    // The account's Epic id was written with the game's token's proof.
    private static bool EpicProved(BsonDocument account) =>
        account.TryGetValue(Identity.IdentityRecord.EpicProvedField, out var at) && at.IsValidDateTime;

    // provisional === true: only a real true counts.
    private static bool IsProvisional(BsonDocument account) =>
        account.TryGetValue("provisional", out var p) && p.IsBoolean && p.AsBoolean;

    private static DateTime LastSeen(BsonDocument account) =>
        account.TryGetValue("lastSeenAt", out var d) && d.IsValidDateTime ? d.ToUniversalTime() : DateTime.UnixEpoch;

    private static string? Str(BsonDocument account, string field) =>
        account.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;
}
