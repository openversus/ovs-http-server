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
// checked against its own kind only. (The TS server checked the /access IP against every file, and read its
// HASHBANS_FILE for the Steam and Epic lists too, so no Steam or Epic ban was ever in effect.)
// Where bans are:
//   Mongo   the source of truth. player_bans: one record per ban the services made (PersonBans), every identifier of
//           the person. bans: single values {kind, value} imported from the hand-edited files (BanStore.ImportAsync)
//           and older entries. Active = no lifted_at: a lifted ban stays, so nothing brings it back.
//   Redis   one set per kind (bans:ip, bans:cidr, bans:steam, bans:epic, bans:hardware, bans:install, bans:player;
//           bans:id is any identifier but the IP): the active values, which every check reads. Filled from Mongo at the
//           access service's start, after each import, and every minute (only added to: a lift removes its own).
//   files   one text file per kind (BanSettings), edited by hand: imported into Mongo at start and when one changes,
//           an entry only when Mongo has no record of that value (active or lifted). The files are never written.
//   the auto-ban file: every ban and lift the services made, appended as evidence (AutoBans.cs). Never read.

/// <summary>Ban files, imported into Mongo (and the auto-ban trail). Paths relative to the working directory; the TS server's variable names fill some.</summary>
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

    [Description("The trail of every ban and lift the services make (YAML, appended to, never read). The services that ban must be able to write it.")]
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

/// <summary>The identifier a ban matched, and where the ban came from (the file it was imported from, a ban record's id).</summary>
public sealed record BanMatch(BanKind Kind, string Value, string Source);

public interface IBanService
{
    /// <summary>Whether the IP is banned (the first check of a login, before anything else is known).</summary>
    Task<bool> IsBannedAsync(string ip);

    /// <summary>The first of <paramref name="who"/>'s identifiers that is banned, or null.</summary>
    Task<BanMatch?> FindAsync(BanIdentifiers who);
}

internal sealed class BanService(IServiceProvider services, ILogger<BanService> log) : IBanService
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

    /// <summary>An identifier as the stores keep it: trimmed; hex ids and player ids in lower case.</summary>
    public static string Canonical(BanKind kind, string value)
    {
        string trimmed = Js.Trim(value);
        return kind is BanKind.Ip or BanKind.Steam ? trimmed : trimmed.ToLowerInvariant();
    }

    public async Task<bool> IsBannedAsync(string ip) => await FindAsync(new BanIdentifiers(Ip: ip)) is not null;

    public async Task<BanMatch?> FindAsync(BanIdentifiers who)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Checking bans with no Redis (REDIS): nothing is banned here");
            return null;
        }

        var mongo = services.GetService<IMongoDatabase>();
        foreach (var (kind, value) in who.Known().Select(k => (k.Kind, Value: Canonical(k.Kind, k.Value))).Where(k => k.Value.Length > 0))
        {
            if (await redis.SetContainsAsync(Key(kind), value) || (kind is not (BanKind.Ip or BanKind.Player) && await redis.SetContainsAsync(IdKey, value)))
            {
                return new BanMatch(kind, value, await BanStore.OriginAsync(mongo, kind, value));
            }

            if (kind == BanKind.Ip && (await redis.SetMembersAsync(CidrKey)).Select(c => c.ToString()).FirstOrDefault(block => InCidr(value, block)) is { } block)
            {
                return new BanMatch(kind, value, $"{await BanStore.OriginAsync(mongo, null, block)} {block}");
            }
        }

        return null;
    }

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
}

/// <summary>The bans in Mongo: importing the hand-edited files, loading the active values into Redis, where one came from.</summary>
internal static class BanStore
{
    /// <summary>Single values: the hand-edited files' entries, imported, and older entries ({kind, value}).</summary>
    public const string Values = "bans";

    /// <summary>Not lifted.</summary>
    public static readonly FilterDefinition<BsonDocument> Active = Builders<BsonDocument>.Filter.Exists("lifted_at", false);

    /// <summary>A kind's name in the bans collection; null for a block (cidr).</summary>
    public static string ValueKind(BanKind? kind) => kind switch
    {
        null => "cidr",
        BanKind.Ip => "ip",
        BanKind.Steam => "steam",
        BanKind.Epic => "epic",
        BanKind.Hardware => "hardware",
        BanKind.Install => "install",
        _ => "player",
    };

    /// <summary>Where a person record keeps an identifier of <paramref name="kind"/>.</summary>
    public static string RecordField(BanKind kind) => kind switch
    {
        BanKind.Ip => "identifiers.ip",
        BanKind.Steam => "identifiers.steam_id",
        BanKind.Epic => "identifiers.epic_id",
        BanKind.Hardware => "identifiers.hardware_id",
        BanKind.Install => "identifiers.install_id",
        _ => "player.id",
    };

    private static string? KeyOf(string valueKind) => valueKind switch
    {
        "ip" => BanService.IpKey,
        "cidr" => BanService.CidrKey,
        "steam" => BanService.Key(BanKind.Steam),
        "epic" => BanService.Key(BanKind.Epic),
        "hardware" => BanService.Key(BanKind.Hardware),
        "install" => BanService.Key(BanKind.Install),
        "id" => BanService.IdKey,
        _ => null,
    };

    /// <summary>The hand-edited files and the kind each holds (null: IP blocks).</summary>
    public static IEnumerable<(BanKind? Kind, string? Path)> Files(BanSettings s) =>
        [(BanKind.Ip, s.IpFile), (null, s.CidrFile), (BanKind.Steam, s.SteamIdFile), (BanKind.Epic, s.EpicIdFile), (BanKind.Hardware, s.HardwareFile), (BanKind.Install, s.InstallIdFile)];

    /// <summary>
    /// A ban file's entries: one per line, trimmed (hex ids in lower case); a line starting with # is a comment, and the
    /// comment line just above an entry is kept with it (the TS server wrote one per ban: when, the player, the reason).
    /// </summary>
    internal static List<(string Value, string Note)> Parse(string text, BanKind? kind)
    {
        var entries = new List<(string, string)>();
        string note = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = Js.Trim(raw);
            if (line.StartsWith('#'))
            {
                note = Js.Trim(line[1..]);
                continue;
            }

            if (line.Length > 0)
            {
                entries.Add((kind is { } k ? BanService.Canonical(k, line) : line, note));
            }

            note = "";
        }

        return entries;
    }

    /// <summary>Creates the bans collection's unique (kind, value) index, so two replicas importing at once cannot both insert a value.</summary>
    public static async Task EnsureIndexAsync(IMongoDatabase mongo, ILogger log, CancellationToken ct)
    {
        try
        {
            await mongo.GetCollection<BsonDocument>(Values).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(new BsonDocument { { "kind", 1 }, { "value", 1 } }, new CreateIndexOptions { Unique = true, Name = "kind_value" }),
                cancellationToken: ct);
        }
        catch (MongoCommandException e)
        {
            log.LogError(e, "The bans collection's unique (kind, value) index could not be made: an import on two replicas at once may store a value twice");
        }
    }

    /// <summary>Imports every ban file's entries that Mongo has no record of (active or lifted). The number inserted.</summary>
    public static async Task<int> ImportAsync(IMongoDatabase mongo, BanSettings current, DateTime now, ILogger log, CancellationToken ct)
    {
        var values = mongo.GetCollection<BsonDocument>(Values);
        int inserted = 0;
        foreach (var (kind, path) in Files(current))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            List<(string Value, string Note)> entries;
            try
            {
                entries = Parse(await File.ReadAllTextAsync(path, ct), kind);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogError(e, "Ban file {Path} could not be read: not imported", Path.GetFullPath(path));
                continue;
            }

            string name = Path.GetFileName(path);
            string valueKind = ValueKind(kind);
            int fromFile = 0;
            foreach (var (value, note) in entries)
            {
                try
                {
                    var result = await values.UpdateOneAsync(
                        new BsonDocument { { "kind", valueKind }, { "value", value } },
                        Builders<BsonDocument>.Update.SetOnInsert("source", "file").SetOnInsert("file", name).SetOnInsert("note", note).SetOnInsert("created_at", now),
                        new UpdateOptions { IsUpsert = true }, ct);
                    if (result.UpsertedId is not null)
                    {
                        fromFile++;
                    }
                }
                catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // Another replica imported it a moment ago.
                }
            }

            if (fromFile > 0)
            {
                log.LogWarning("Imported {Count} new ban(s) from {Path} ({Total} entries)", fromFile, Path.GetFullPath(path), entries.Count);
            }

            inserted += fromFile;
        }

        return inserted;
    }

    /// <summary>Adds every active value (bans, and every identifier of each active person record) to the Redis sets. Only adds; the number added.</summary>
    public static async Task<long> LoadAsync(IMongoDatabase mongo, IDatabase redis, ILogger log, CancellationToken ct)
    {
        var byKey = new Dictionary<string, HashSet<RedisValue>>();
        void Add(string? key, string value)
        {
            if (key is not null && value.Length > 0)
            {
                (byKey.TryGetValue(key, out var set) ? set : byKey[key] = []).Add(value);
            }
        }

        using (var cursor = await mongo.GetCollection<BsonDocument>(Values).FindAsync(Active, cancellationToken: ct))
        {
            await cursor.ForEachAsync(ban =>
            {
                if (ban.GetValue("kind", BsonNull.Value) is { IsString: true } kind && ban.GetValue("value", BsonNull.Value) is { IsString: true } value)
                {
                    Add(KeyOf(kind.AsString), value.AsString);
                }
            }, ct);
        }

        using (var cursor = await mongo.GetCollection<BsonDocument>(PersonBans.Collection).FindAsync(Active, cancellationToken: ct))
        {
            await cursor.ForEachAsync(record =>
            {
                foreach (var (kind, value) in PersonBans.IdentifiersOf(record).Known())
                {
                    Add(BanService.Key(kind), BanService.Canonical(kind, value));
                }
            }, ct);
        }

        long added = 0;
        foreach (var (key, values) in byKey)
        {
            added += await redis.SetAddAsync(key, [.. values]);
        }

        if (added > 0)
        {
            log.LogInformation("Loaded bans into Redis: {Added} added ({Counts})", added, string.Join(", ", byKey.Select(c => $"{c.Key} {c.Value.Count}")));
        }

        return added;
    }

    /// <summary>Where an active ban of a value came from: the file it was imported from, or the ban record's id.</summary>
    public static async Task<string> OriginAsync(IMongoDatabase? mongo, BanKind? kind, string value)
    {
        if (mongo is null)
        {
            return "redis";
        }

        try
        {
            var single = await mongo.GetCollection<BsonDocument>(Values)
                .Find(Active & Builders<BsonDocument>.Filter.In("kind", kind is BanKind.Ip or null ? new[] { ValueKind(kind) } : new[] { ValueKind(kind), "id" }) & Builders<BsonDocument>.Filter.Eq("value", value))
                .FirstOrDefaultAsync();
            if (single is not null)
            {
                return single.GetValue("file", BsonNull.Value) is { IsString: true, AsString.Length: > 0 } file ? file.AsString : Values;
            }

            if (kind is { } k && await mongo.GetCollection<BsonDocument>(PersonBans.Collection)
                .Find(Active & Builders<BsonDocument>.Filter.Eq(RecordField(k), value)).FirstOrDefaultAsync() is { } record)
            {
                return $"ban {record.GetValue("ban_id", "").AsString}";
            }
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            // Only the label: the ban stands either way.
        }

        return "redis";
    }
}

/// <summary>At the access service's start: imports the ban files into Mongo and loads the active bans into Redis.</summary>
internal sealed class BanLoader(IServiceProvider services, IOptionsMonitor<BanSettings> settings, TimeProvider time, ILogger<BanLoader> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        AutoBans.CheckWritable(settings.CurrentValue.AutoBansFile, log);
        if (services.GetService<IMongoDatabase>() is not { } mongo || services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            return;
        }

        try
        {
            await BanStore.EnsureIndexAsync(mongo, log, ct);
            await BanStore.ImportAsync(mongo, settings.CurrentValue, time.GetUtcNow().UtcDateTime, log, ct);
            if (await BanStore.LoadAsync(mongo, multiplexer.GetDatabase(), log, ct) > 0)
            {
                // The other services' caches reread now, not at their next minute (after a Redis flush, say).
                await multiplexer.GetDatabase().PublishAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), BanEvent.Loaded.ToString());
            }
        }
        catch (Exception e) when (e is MongoException or RedisException or TimeoutException)
        {
            // The sweep loads again every minute.
            log.LogError(e, "Importing and loading the bans failed; the sweep tries again");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
