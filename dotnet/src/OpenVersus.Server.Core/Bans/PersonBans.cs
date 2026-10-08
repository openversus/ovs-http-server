using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Bans;

/// <summary>A ban to make: whose, why, how it was found, and the request that did it (when one did).</summary>
public sealed record BanRequest(
    string PlayerId,
    string Reason,
    string Source,
    string AttemptedName = "",
    string MatchedList = "",
    string MatchedTerm = "",
    string RequestIp = "",
    string UserAgent = "");

/// <summary>A lift made: the ban records lifted, the identifiers no longer banned, and those still banned and why.</summary>
public sealed record LiftResult(string PlayerId, string Name, IReadOnlyList<string> LiftedBans, IReadOnlyList<string> NoLongerBanned, IReadOnlyList<string> StillBanned);

/// <summary>A lift of single values: those lifted (with the file each came from), those no longer banned, those still banned and why, and those that had no active entry.</summary>
public sealed record ValueLiftResult(IReadOnlyList<string> Lifted, IReadOnlyList<string> NoLongerBanned, IReadOnlyList<string> StillBanned, IReadOnlyList<string> NotFound);

public interface IPersonBans
{
    /// <summary>
    /// Bans the person behind a player through every identifier known for them, cuts their connection, and records it;
    /// null when there is no such player.
    /// </summary>
    Task<BanRecord?> BanAsync(BanRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lifts the player's ban records (they stay, marked lifted); takes out of effect each of their identifiers that no
    /// other active ban holds, and reports the ones still held (a hand-edited file's entry is never lifted here: it is
    /// removed from its file). Null when Mongo, the source of truth, is not configured.
    /// </summary>
    Task<LiftResult?> LiftAsync(string playerId, string reason, string source, CancellationToken ct = default);

    /// <summary>
    /// Lifts single values ({kind, value} in the bans collection: a ban file's entries); each is let through unless another
    /// active ban holds it. Null when Mongo is not configured.
    /// </summary>
    Task<ValueLiftResult?> LiftValuesAsync(IReadOnlyList<(string Kind, string Value)> values, string reason, string source, CancellationToken ct = default);

    /// <summary>Lifts every single value (a ban file's entry) that is one of the player's identifiers, past or present. IP blocks are left: they cover others.</summary>
    Task<ValueLiftResult?> LiftValuesOfAsync(string playerId, string reason, string source, CancellationToken ct = default);
}

// A ban takes effect first and is recorded after: the Redis sets (every replica's check, and the player id that refuses
// a still-valid session token), bans:changed (the token caches and the sweep), the disconnect; then the record in Mongo
// (player_bans) and in the auto-ban file. Each step is attempted whatever the others did, and a failed one is an error
// in the log: the bans are permanent and must not depend on a single store.
internal sealed class PersonBans(IServiceProvider services, IOptionsMonitor<BanSettings> settings, TimeProvider time, ILogger<PersonBans> log) : IPersonBans
{
    public const string Collection = "player_bans";

    public async Task<BanRecord?> BanAsync(BanRequest request, CancellationToken ct = default)
    {
        var mongo = services.GetService<IMongoDatabase>();
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        if (!ObjectId.TryParse(request.PlayerId, out var id))
        {
            return null;
        }

        BsonDocument? player = mongo is null ? null
            : await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        if (player is null && mongo is not null)
        {
            return null;
        }

        // The request's IP is the one in use now (the account's may be older).
        var who = new BanIdentifiers(PlayerId: id.ToString());
        if (player is not null)
        {
            who = BanIdentifiers.OfPlayer(player);
            if (request.RequestIp.Length > 0)
            {
                who = who with { Ip = request.RequestIp };
            }
        }

        bool online = false, disconnected = false;
        if (redis is not null)
        {
            try
            {
                foreach (var (kind, value) in who.Known())
                {
                    await redis.SetAddAsync(BanService.Key(kind), BanService.Canonical(kind, value));
                }

                await redis.PublishAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), BanEvent.Banned(who.PlayerId).ToString());
                online = await redis.SetContainsAsync("online_players", who.PlayerId);
                disconnected = await PlayerMessages.DisconnectAsync(redis, new JsonObject { ["playerId"] = who.PlayerId }) > 0 && online;
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogError(e, "Banning player {Player}: Redis failed (the ban is recorded below, and loaded into Redis at the next start)", who.PlayerId);
            }
        }
        else
        {
            log.LogError("Banning player {Player}: this service has no Redis, so the ban is not in effect until a service with Redis loads it", who.PlayerId);
        }

        var record = new BanRecord(Guid.NewGuid().ToString(), time.GetUtcNow(), request.Reason, request.Source, who,
            NameAtBan: Str(player, "name"), AttemptedName: request.AttemptedName,
            PlayerCreatedAt: id.CreationTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            MatchedList: request.MatchedList, MatchedTerm: request.MatchedTerm,
            RequestIp: request.RequestIp, UserAgent: request.UserAgent, Online: online, Disconnected: disconnected);

        if (mongo is not null)
        {
            try
            {
                await mongo.GetCollection<BsonDocument>(Collection).InsertOneAsync(ToBson(record), cancellationToken: ct);
            }
            catch (Exception e) when (e is MongoException or TimeoutException)
            {
                log.LogError(e, "Ban {Ban} of player {Player} could not be stored in Mongo", record.BanId, who.PlayerId);
            }
        }

        if (settings.CurrentValue.AutoBansFile is { Length: > 0 } file)
        {
            try
            {
                AutoBans.Append(file, AutoBans.ToYaml(record));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogError(e, "Ban {Ban} of player {Player} could not be appended to {Path}", record.BanId, who.PlayerId, Path.GetFullPath(file));
            }
        }

        log.LogWarning("BANNED player {Player} (\"{Name}\") [{Source}: {Reason}; {List} {Term}; attempted \"{Attempted}\"]: {Identifiers}; online {Online}, disconnected {Disconnected}",
            who.PlayerId, record.NameAtBan, request.Source, request.Reason, request.MatchedList, request.MatchedTerm, request.AttemptedName,
            string.Join(", ", who.Known().Select(k => $"{k.Kind} {k.Value}")), online, disconnected);
        return record;
    }

    public async Task<LiftResult?> LiftAsync(string playerId, string reason, string source, CancellationToken ct = default)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || !ObjectId.TryParse(playerId, out var id))
        {
            return null;
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        var records = mongo.GetCollection<BsonDocument>(Collection);
        string player = id.ToString();
        var account = await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        var lifted = await records.Find(BanStore.Active & Builders<BsonDocument>.Filter.Eq("player.id", player)).ToListAsync(ct);
        var now = time.GetUtcNow();
        if (lifted.Count > 0)
        {
            await records.UpdateManyAsync(Builders<BsonDocument>.Filter.In("_id", lifted.Select(r => r["_id"])),
                Builders<BsonDocument>.Update.Set("lifted_at", now.UtcDateTime).Set("lifted_reason", reason).Set("lifted_source", source), cancellationToken: ct);
        }

        // Their identifiers: what the lifted records banned, and what the account has now.
        var identifiers = lifted.SelectMany(r => IdentifiersOf(r).Known())
            .Concat(account is null ? [(BanKind.Player, player)] : BanIdentifiers.OfPlayer(account).Known())
            .Select(k => (k.Kind, Value: BanService.Canonical(k.Kind, k.Value))).Distinct().ToList();
        var noLonger = new List<string>();
        var still = new List<string>();
        foreach (var (kind, value) in identifiers)
        {
            string label = $"{kind} {value}";
            if (await HolderAsync(mongo, kind, value, ct) is { } holder)
            {
                still.Add($"{label} ({holder})");
            }
            else
            {
                noLonger.Add(label);
                await LetThroughAsync(redis, BanService.Key(kind), kind is not (BanKind.Ip or BanKind.Player), value);
            }
        }

        if (redis is not null)
        {
            await redis.PublishAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), BanEvent.Lifted(player).ToString());
        }

        string name = account?.GetValue("name", "").AsString ?? "";
        var banIds = lifted.Select(r => r.GetValue("ban_id", "").AsString).ToList();
        if (settings.CurrentValue.AutoBansFile is { Length: > 0 } file)
        {
            try
            {
                AutoBans.Append(file, AutoBans.LiftToYaml(now, reason, source, player, name, banIds, [], noLonger, still));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogError(e, "The lift for player {Player} could not be appended to {Path}", player, Path.GetFullPath(file));
            }
        }

        log.LogWarning("LIFTED ban(s) [{Bans}] of player {Player} (\"{Name}\") [{Source}: {Reason}]; no longer banned: {NoLonger}; still banned: {Still}",
            string.Join(", ", banIds), player, name, source, reason, string.Join(", ", noLonger), still.Count == 0 ? "nothing" : string.Join(", ", still));
        return new LiftResult(player, name, banIds, noLonger, still);
    }

    public async Task<ValueLiftResult?> LiftValuesOfAsync(string playerId, string reason, string source, CancellationToken ct = default)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || !ObjectId.TryParse(playerId, out var id))
        {
            return null;
        }

        // Their identifiers, from every ban record of theirs (lifted too) and their account.
        var records = await mongo.GetCollection<BsonDocument>(Collection).Find(Builders<BsonDocument>.Filter.Eq("player.id", id.ToString())).ToListAsync(ct);
        var account = await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        var identifiers = records.SelectMany(r => IdentifiersOf(r).Known()).Concat(account is null ? [] : BanIdentifiers.OfPlayer(account).Known())
            .Where(k => k.Kind != BanKind.Player).Select(k => (k.Kind, Value: BanService.Canonical(k.Kind, k.Value))).Distinct().ToList();
        var entries = new List<(string Kind, string Value)>();
        foreach (var (kind, value) in identifiers)
        {
            if (await mongo.GetCollection<BsonDocument>(BanStore.Values).Find(BanStore.Active & Builders<BsonDocument>.Filter.In("kind", ValueKinds(kind))
                    & Builders<BsonDocument>.Filter.Eq("value", value)).FirstOrDefaultAsync(ct) is not null)
            {
                entries.Add((BanStore.ValueKind(kind), value));
            }
        }

        var lift = await LiftValuesAsync(entries, reason, source, ct);
        // An IP of theirs inside a block: the block stays (it covers others), and the IP with it.
        var blocked = new List<string>();
        foreach (var (_, ip) in identifiers.Where(k => k.Kind == BanKind.Ip))
        {
            if (!lift!.StillBanned.Any(s => s.StartsWith($"ip {ip} ", StringComparison.Ordinal)) && await HolderAsync(mongo, BanKind.Ip, ip, ct) is { } holder)
            {
                blocked.Add($"ip {ip} ({holder})");
            }
        }

        return lift! with { StillBanned = [.. lift.StillBanned, .. blocked] };
    }

    public async Task<ValueLiftResult?> LiftValuesAsync(IReadOnlyList<(string Kind, string Value)> entries, string reason, string source, CancellationToken ct = default)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return null;
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        var values = mongo.GetCollection<BsonDocument>(BanStore.Values);
        var now = time.GetUtcNow();
        var lifted = new List<string>();
        var noLonger = new List<string>();
        var still = new List<string>();
        var notFound = new List<string>();
        foreach (var (valueKind, value) in entries.Distinct())
        {
            BanKind? kind = KindOf(valueKind);
            // A Steam, Epic, hardware or install id is lifted from the older any-identifier entries too.
            string[] kinds = kind is { } k ? ValueKinds(k) : [valueKind];
            var filter = BanStore.Active & Builders<BsonDocument>.Filter.In("kind", kinds) & Builders<BsonDocument>.Filter.Eq("value", value);
            var docs = await values.Find(filter).ToListAsync(ct);
            if (docs.Count == 0)
            {
                notFound.Add($"{valueKind} {value}");
                continue;
            }

            await values.UpdateManyAsync(filter, Builders<BsonDocument>.Update.Set("lifted_at", now.UtcDateTime).Set("lifted_reason", reason).Set("lifted_source", source), cancellationToken: ct);
            string label = $"{valueKind} {value}";
            lifted.Add($"{label} ({string.Join(", ", docs.Select(d => d.GetValue("file", BanStore.Values).AsString).Distinct())})");
            if (await HolderAsync(mongo, kind, value, ct) is { } holder)
            {
                still.Add($"{label} ({holder})");
            }
            else
            {
                noLonger.Add(label);
                await LetThroughAsync(redis, kind is { } key ? BanService.Key(key) : valueKind == "cidr" ? BanService.CidrKey : BanService.IdKey,
                    kind is not (null or BanKind.Ip), value);
            }
        }

        if (lifted.Count > 0 && settings.CurrentValue.AutoBansFile is { Length: > 0 } file)
        {
            try
            {
                AutoBans.Append(file, AutoBans.LiftToYaml(now, reason, source, "", "", [], lifted, noLonger, still));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogError(e, "A lift of single values could not be appended to {Path}", Path.GetFullPath(file));
            }
        }

        if (lifted.Count > 0)
        {
            log.LogWarning("LIFTED single ban value(s) [{Source}: {Reason}]: {Lifted}; no longer banned: {NoLonger}; still banned: {Still}",
                source, reason, string.Join(", ", lifted), string.Join(", ", noLonger), still.Count == 0 ? "nothing" : string.Join(", ", still));
        }

        return new ValueLiftResult(lifted, noLonger, still, notFound);
    }

    // What still bans a value after a lift: an active single value (a file's entry), another person's active record, or
    // (an IP) an active block containing it. Null when nothing does.
    private static async Task<string?> HolderAsync(IMongoDatabase mongo, BanKind? kind, string value, CancellationToken ct)
    {
        var values = mongo.GetCollection<BsonDocument>(BanStore.Values);
        string[] kinds = kind is { } k ? ValueKinds(k) : ["cidr"];
        if (await values.Find(BanStore.Active & Builders<BsonDocument>.Filter.In("kind", kinds) & Builders<BsonDocument>.Filter.Eq("value", value)).FirstOrDefaultAsync(ct) is { } single)
        {
            return $"{single.GetValue("file", BanStore.Values).AsString}: ovsctl bans lift {single["kind"].AsString} {value}";
        }

        if (kind is { } recordKind && await mongo.GetCollection<BsonDocument>(Collection)
                .Find(BanStore.Active & Builders<BsonDocument>.Filter.Eq(BanStore.RecordField(recordKind), value)).FirstOrDefaultAsync(ct) is { } other)
        {
            return $"ban {other.GetValue("ban_id", "").AsString} of player {other["player"]["id"].AsString}";
        }

        if (kind == BanKind.Ip)
        {
            foreach (var block in await values.Find(BanStore.Active & Builders<BsonDocument>.Filter.Eq("kind", "cidr")).ToListAsync(ct))
            {
                string cidr = block.GetValue("value", "").AsString;
                if (BanService.InCidr(value, cidr))
                {
                    return $"{block.GetValue("file", BanStore.Values).AsString} {cidr}: ovsctl bans lift cidr {cidr}";
                }
            }
        }

        return null;
    }

    private static async Task LetThroughAsync(IDatabase? redis, string key, bool anyIdentifier, string value)
    {
        if (redis is null)
        {
            return;
        }

        await redis.SetRemoveAsync(key, value);
        if (anyIdentifier)
        {
            await redis.SetRemoveAsync(BanService.IdKey, value);
        }
    }

    // The single-value kinds that ban an identifier of this kind: its own, and the older any-identifier "id" but for an IP.
    private static string[] ValueKinds(BanKind kind) => kind is BanKind.Ip or BanKind.Player ? [BanStore.ValueKind(kind)] : [BanStore.ValueKind(kind), "id"];

    /// <summary>The identifier kind of a single value's kind; null for an IP block or an any-identifier entry.</summary>
    internal static BanKind? KindOf(string valueKind) => valueKind switch
    {
        "ip" => BanKind.Ip,
        "steam" => BanKind.Steam,
        "epic" => BanKind.Epic,
        "hardware" => BanKind.Hardware,
        "install" => BanKind.Install,
        _ => null,
    };

    /// <summary>A record's banned identifiers, as BanStore loads them.</summary>
    internal static BanIdentifiers IdentifiersOf(BsonDocument record)
    {
        var ids = record.GetValue("identifiers", BsonNull.Value) as BsonDocument;
        var player = record.GetValue("player", BsonNull.Value) as BsonDocument;
        return new BanIdentifiers(Str(ids, "ip"), Str(ids, "steam_id"), Str(ids, "epic_id"), Str(ids, "hardware_id"), Str(ids, "install_id"), Str(player, "id"));
    }

    internal static BsonDocument ToBson(BanRecord r) => new()
    {
        { "ban_id", r.BanId },
        { "at", r.At.UtcDateTime },
        { "reason", r.Reason },
        { "source", r.Source },
        { "player", new BsonDocument { { "id", r.Who.PlayerId }, { "name_at_ban", r.NameAtBan }, { "attempted_name", r.AttemptedName }, { "created_at", r.PlayerCreatedAt } } },
        { "matched", new BsonDocument { { "list", r.MatchedList }, { "term", r.MatchedTerm } } },
        {
            "identifiers", new BsonDocument
            {
                { "ip", r.Who.Ip }, { "steam_id", r.Who.SteamId }, { "epic_id", r.Who.EpicId }, { "hardware_id", r.Who.HardwareId }, { "install_id", r.Who.InstallId },
            }
        },
        { "request", new BsonDocument { { "ip", r.RequestIp }, { "user_agent", r.UserAgent } } },
        { "online", r.Online },
        { "disconnected", r.Disconnected },
    };

    private static string Str(BsonDocument? doc, string field) => doc is not null && doc.TryGetValue(field, out var v) && v.IsString ? v.AsString : "";
}
