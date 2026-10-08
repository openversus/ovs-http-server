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

/// <summary>
/// A ban to make: whose, why, how it was found, and the request that did it (when one did). <see cref="WholePerson"/>
/// false bans only the player id (the player was found through an identifier that is banned already).
/// </summary>
public sealed record BanRequest(
    string PlayerId,
    string Reason,
    string Source,
    string AttemptedName = "",
    string MatchedList = "",
    string MatchedTerm = "",
    string RequestIp = "",
    string UserAgent = "",
    bool WholePerson = true);

public interface IPersonBans
{
    /// <summary>
    /// Bans the person behind a player through every identifier known for them, cuts their connection, and records it;
    /// null when there is no such player.
    /// </summary>
    Task<BanRecord?> BanAsync(BanRequest request, CancellationToken ct = default);
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
        if (request.WholePerson && player is not null)
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

                await redis.PublishAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), who.PlayerId);
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
                AutoBans.Append(file, record);
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

    /// <summary>A record's banned identifiers, as BanLoader reads them back.</summary>
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
