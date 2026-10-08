using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Bans;

// A ban is on a PERSON, through every identifier known for them: the IP, the Steam id, the Epic id, the hardware hash,
// the install id, and the player id (which is what refuses a session token that is still valid). Each identifier is
// checked against its own lists only. The TS server checked the /access IP against every file, and read its
// HASHBANS_FILE for the Steam and Epic lists too, so no Steam or Epic ban was ever in effect.
// Where bans come from, all merged:
//   files   one text file per kind, edited by hand (BanSettings: IP and CIDR, Steam, Epic, hardware, install). One entry
//           per line, # starts a comment line; entries are trimmed, hex ids compared in lower case. Reread when changed.
//   auto    AutoBansFile: the record of every ban the services made (AutoBans.cs), appended to, never rewritten.
//   Mongo   player_bans: the same records (PersonBans), and bans: {kind, value} single values.
//   Redis   one set per kind (bans:ip, bans:cidr, bans:steam, ...) shared by every replica at once; bans:id is any
//           identifier but the IP. BanLoader fills the sets from Mongo and the auto file at startup, so either one alone
//           restores every ban.

/// <summary>Ban sources. File paths relative to the working directory; the TS server's variable names fill some.</summary>
public sealed class BanSettings
{
    [Description("IP bans, one address per line (IP_BANS_FILE).")]
    public string? IpFile { get; set; } = "../data/bans.txt";

    [Description("IP block bans, IPv4 blocks like 203.0.113.0/24; a line without a prefix is one address (CIDR_BANS_FILE).")]
    public string? CidrFile { get; set; } = "../data/cidr_bans.txt";

    [Description("Steam id bans, one id per line.")]
    public string? SteamIdFile { get; set; } = "../data/steamid_bans.txt";

    [Description("Epic id bans, one id per line.")]
    public string? EpicIdFile { get; set; } = "../data/epicid_bans.txt";

    [Description("Hardware hash bans, one hash per line (HASHBANS_FILE). Only a strong version-2 hash is ever matched or banned.")]
    public string? HardwareFile { get; set; } = "../data/hashbans.txt";

    [Description("Install id bans, one id per line.")]
    public string? InstallIdFile { get; set; } = "../data/installid_bans.txt";

    [Description("The record of every ban the services make (YAML, appended to). Read as a ban source too; the services that ban must be able to write it.")]
    public string? AutoBansFile { get; set; } = "../data/auto_bans.yaml";

    [Description("Banned name terms (YAML `terms:` list): a name containing one anywhere bans the person.")]
    public string? BannedNamesFile { get; set; } = "../data/banned_names.yaml";

    [Description("Force-change name terms (YAML `terms:` list): a name containing one as a whole word must change.")]
    public string? ForceChangeNamesFile { get; set; } = "../data/force_change_names.yaml";

    [Description("Allowed words (YAML `terms:` list): words containing a banned or force-change term that are fine.")]
    public string? AllowedNamesFile { get; set; } = "../data/allowed_names.yaml";
}

public enum BanKind
{
    Ip,
    Steam,
    Epic,
    Hardware,
    Install,
    Player,
}

/// <summary>What identifies a person; "" for what is not known. Ids as IdentityRules normalizes them.</summary>
public sealed record BanIdentifiers(string Ip = "", string SteamId = "", string EpicId = "", string HardwareId = "", string InstallId = "", string PlayerId = "")
{
    public IEnumerable<(BanKind Kind, string Value)> Known()
    {
        (BanKind, string)[] all = [(BanKind.Ip, Ip), (BanKind.Steam, SteamId), (BanKind.Epic, EpicId), (BanKind.Hardware, HardwareId), (BanKind.Install, InstallId), (BanKind.Player, PlayerId)];
        return all.Where(k => k.Item2.Length > 0);
    }

    /// <summary>A player record's identifiers as a ban checks and records them: the account's current IP (else its IP), ids as IdentityRules keeps them (the hardware hash only when strong).</summary>
    public static BanIdentifiers OfPlayer(BsonDocument player)
    {
        static string S(BsonDocument? d, string f) => d is not null && d.TryGetValue(f, out var v) && v.IsString ? v.AsString : "";
        string ip = S(player.GetValue("account", BsonNull.Value) as BsonDocument, "current_ip") is { Length: > 0 } current ? current : S(player, "ip");
        return new BanIdentifiers(ip,
            Access.IdentityRules.Normalize(Access.IdentityKind.Steam, S(player, "steamId")),
            Access.IdentityRules.Normalize(Access.IdentityKind.Epic, S(player, "epicId")),
            Access.IdentityRules.NormalizeHardware(S(player, "hardwareId"), S(player, "hardwareIdVersion"), S(player, "hardwareIdQuality")).HardwareId,
            Access.IdentityRules.Normalize(Access.IdentityKind.Install, S(player, "installId")),
            player.GetValue("_id", BsonNull.Value) is { IsObjectId: true } id ? id.AsObjectId.ToString() : "");
    }
}

/// <summary>The identifier a ban matched, and where the ban is (a file's name, or redis).</summary>
public sealed record BanMatch(BanKind Kind, string Value, string Source);

public interface IBanService
{
    /// <summary>Whether the IP is banned (the first check of a login, before anything else is known).</summary>
    Task<bool> IsBannedAsync(string ip);

    /// <summary>The first of <paramref name="who"/>'s identifiers that is banned, or null.</summary>
    Task<BanMatch?> FindAsync(BanIdentifiers who);
}

internal sealed class BanService(IServiceProvider services, IOptionsMonitor<BanSettings> settings, TimeProvider time, ILogger<BanService> log) : IBanService
{
    public const string IpKey = "bans:ip";
    public const string CidrKey = "bans:cidr";
    public const string IdKey = "bans:id";

    /// <summary>The Redis set holding banned identifiers of <paramref name="kind"/>.</summary>
    public static string Key(BanKind kind) => kind switch
    {
        BanKind.Ip => IpKey,
        BanKind.Steam => "bans:steam",
        BanKind.Epic => "bans:epic",
        BanKind.Hardware => "bans:hardware",
        BanKind.Install => "bans:install",
        _ => BannedPlayers.Key,
    };

    /// <summary>An identifier as the lists keep it: trimmed; hex ids and player ids in lower case.</summary>
    public static string Canonical(BanKind kind, string value)
    {
        string trimmed = Js.Trim(value);
        return kind is BanKind.Ip or BanKind.Steam ? trimmed : trimmed.ToLowerInvariant();
    }

    private readonly ConcurrentDictionary<(string, BanKind), BanFile> _files = new();
    private readonly AutoBans.Reader _auto = new();

    public async Task<bool> IsBannedAsync(string ip) => await FindAsync(new BanIdentifiers(Ip: ip)) is not null;

    public async Task<BanMatch?> FindAsync(BanIdentifiers who)
    {
        var current = settings.CurrentValue;
        var known = who.Known().Select(k => (k.Kind, Value: Canonical(k.Kind, k.Value))).Where(k => k.Value.Length > 0).ToList();
        foreach (var (kind, value) in known)
        {
            if (FileFor(current, kind) is { } file && Read(file, kind).Contains(value))
            {
                return new BanMatch(kind, value, Path.GetFileName(file));
            }

            if (kind == BanKind.Ip && Read(current.CidrFile, kind).FirstOrDefault(block => InCidr(value, block)) is { } block)
            {
                return new BanMatch(kind, value, $"{Path.GetFileName(current.CidrFile)} {block}");
            }
        }

        var auto = _auto.Get(current.AutoBansFile, time.GetUtcNow().UtcDateTime, log);
        foreach (var (kind, value) in known)
        {
            if (auto.Contains(kind, value))
            {
                return new BanMatch(kind, value, Path.GetFileName(current.AutoBansFile)!);
            }
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        foreach (var (kind, value) in known)
        {
            if (await redis.SetContainsAsync(Key(kind), value) || (kind is not (BanKind.Ip or BanKind.Player) && await redis.SetContainsAsync(IdKey, value)))
            {
                return new BanMatch(kind, value, "redis");
            }

            if (kind == BanKind.Ip && (await redis.SetMembersAsync(CidrKey)).Select(c => c.ToString()).FirstOrDefault(block => InCidr(value, block)) is { } block)
            {
                return new BanMatch(kind, value, $"redis {block}");
            }
        }

        return null;
    }

    /// <summary>Every hand-edited ban file, for the sweep's change check.</summary>
    internal static IEnumerable<string?> Files(BanSettings s) => [s.IpFile, s.CidrFile, s.SteamIdFile, s.EpicIdFile, s.HardwareFile, s.InstallIdFile, s.AutoBansFile];

    private static string? FileFor(BanSettings s, BanKind kind) => kind switch
    {
        BanKind.Ip => s.IpFile,
        BanKind.Steam => s.SteamIdFile,
        BanKind.Epic => s.EpicIdFile,
        BanKind.Hardware => s.HardwareFile,
        BanKind.Install => s.InstallIdFile,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="ip"/> is in <paramref name="block"/>. IPv4 only, as in the TS server; a block without a
    /// prefix length is that one address (there, NaN makes the mask all ones). Anything unreadable matches nothing.
    /// </summary>
    internal static bool InCidr(string ip, string block)
    {
        string[] parts = block.Split('/');
        int prefix = 32;
        if (parts.Length > 2 || (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)))
        {
            return false;
        }

        if (!TryIPv4(ip, out uint address) || !TryIPv4(parts[0], out uint network))
        {
            return false;
        }

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (address & mask) == (network & mask);
    }

    private static bool TryIPv4(string text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork || text.Count(c => c == '.') != 3)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }

    private IReadOnlyCollection<string> Read(string? path, BanKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var file = _files.GetOrAdd((Path.GetFullPath(path), kind), key => new BanFile(key.Item1, key.Item2));
        return file.Entries(log);
    }

    private sealed class BanFile(string path, BanKind kind)
    {
        private readonly Lock _lock = new();
        private DateTime _stamp = DateTime.MinValue;
        private bool _missingLogged;
        private HashSet<string> _entries = [];

        public IReadOnlyCollection<string> Entries(ILogger log)
        {
            lock (_lock)
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    if (!_missingLogged)
                    {
                        log.LogInformation("Ban file {Path} does not exist; no bans from it", path);
                        _missingLogged = true;
                    }

                    _entries = [];
                    _stamp = DateTime.MinValue;
                    return _entries;
                }

                _missingLogged = false;
                if (info.LastWriteTimeUtc != _stamp)
                {
                    try
                    {
                        _entries = [.. File.ReadAllText(path).Split('\n')
                            .Where(line => !Js.Trim(line).StartsWith('#'))
                            .Select(line => Canonical(kind, line))
                            .Where(line => line.Length > 0)];
                        _stamp = info.LastWriteTimeUtc;
                        log.LogInformation("Ban file {Path}: {Count} entries", path, _entries.Count);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // The last entries read stay; tried again on the next check.
                        log.LogError(e, "Ban file {Path} could not be read: keeping its last {Count} entries", path, _entries.Count);
                    }
                }

                return _entries;
            }
        }
    }
}

/// <summary>At startup, adds the bans stored in Mongo and in the auto-ban file to the Redis sets every replica reads.</summary>
internal sealed class BanLoader(IServiceProvider services, IOptionsMonitor<BanSettings> settings, ILogger<BanLoader> log) : IHostedService
{
    public const string Collection = "bans";

    public async Task StartAsync(CancellationToken ct)
    {
        AutoBans.CheckWritable(settings.CurrentValue.AutoBansFile, log);
        if (services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            return;
        }

        try
        {
            await LoadAsync(services.GetService<IMongoDatabase>(), multiplexer.GetDatabase(), settings.CurrentValue, log, ct);
        }
        catch (Exception e) when (e is MongoException or RedisException or TimeoutException)
        {
            // A replica that cannot load them still serves: the other replicas loaded them, and the sets persist.
            log.LogError(e, "Loading bans into Redis failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Adds every ban in Mongo (bans, player_bans) and in the auto-ban file to the Redis sets. Only adds.</summary>
    internal static async Task LoadAsync(IMongoDatabase? mongo, IDatabase redis, BanSettings current, ILogger log, CancellationToken ct)
    {
        var byKey = new Dictionary<string, HashSet<RedisValue>>();
        void Add(string key, string value)
        {
            if (value.Length > 0)
            {
                (byKey.TryGetValue(key, out var set) ? set : byKey[key] = []).Add(value);
            }
        }

        if (mongo is not null)
        {
            using var cursor = await mongo.GetCollection<BsonDocument>(Collection).FindAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            await cursor.ForEachAsync(ban =>
            {
                string? key = ban.GetValue("kind", BsonNull.Value).ToString() switch
                {
                    "ip" => BanService.IpKey,
                    "cidr" => BanService.CidrKey,
                    "id" => BanService.IdKey,
                    _ => null,
                };
                if (key is not null && ban.GetValue("value", BsonNull.Value) is { IsString: true } value)
                {
                    Add(key, value.AsString);
                }
            }, ct);

            using var records = await mongo.GetCollection<BsonDocument>(PersonBans.Collection).FindAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            await records.ForEachAsync(record =>
            {
                foreach (var (kind, value) in PersonBans.IdentifiersOf(record).Known())
                {
                    Add(BanService.Key(kind), BanService.Canonical(kind, value));
                }
            }, ct);
        }

        if (!string.IsNullOrWhiteSpace(current.AutoBansFile) && File.Exists(current.AutoBansFile))
        {
            try
            {
                foreach (var who in AutoBans.Parse(File.ReadAllText(current.AutoBansFile)))
                {
                    foreach (var (kind, value) in who.Known())
                    {
                        Add(BanService.Key(kind), BanService.Canonical(kind, value));
                    }
                }
            }
            catch (Exception e) when (e is FormatException or YamlDotNet.Core.YamlException or IOException or UnauthorizedAccessException)
            {
                log.LogError(e, "The auto-ban file {Path} could not be read: its bans are not loaded into Redis", current.AutoBansFile);
            }
        }

        foreach (var (key, values) in byKey)
        {
            await redis.SetAddAsync(key, [.. values]);
        }

        log.LogInformation("Loaded bans into Redis: {Counts}", byKey.Count == 0 ? "none" : string.Join(", ", byKey.Select(c => $"{c.Key} {c.Value.Count}")));
    }
}
