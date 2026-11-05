using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Friends;

// The friends reads the game makes at login, ported from the TS server's modules/friends (friends.routes.ts,
// friends.service.ts) and services/friendService.ts (branch infinity-war). The TS services still running beside this
// one read and write the same data, so it is read and written exactly as the TS server does; tools/friends/
// friends_diff.mjs compares the two servers key for key.
//
// Mongo, read
//   friendlists      { accountId, friends: [{ friendAccountId, friendUsername, status, addedAt }] } through mongoose:
//                    an entry without status is "active", without addedAt is "now" (schema defaults on load)
//   friendrequests   { fromAccountId, fromUsername, toAccountId, toUsername, status } read raw (.lean()): a missing
//                    field is a missing key in the answer
//   playertesters    blockedPlayers
// Mongo, written
//   friendlists      a new { accountId, friends: [], _id, __v: 0 } the first time /friends/me (always) or
//                    /social/me/blocked (when the player exists) runs for an account
//   playertesters    blocked ids from the friend list that blockedPlayers lacks: $push $each + $inc __v, with the
//                    defaults of any missing schema field $set (see PlayerRecord)
// Redis, written
//   player:{id}:blocked   the whole blockedPlayers array as JSON, no TTL, when the copy added anything
//
// Differences from the TS server, none visible in the answers or the stored data of a real player: a session whose
// id is not a player id (an /api/identify token) gets an empty answer and creates nothing (the TS server creates a
// friend list for that id, and for a token with no id at all its queries drop the filter and read other players'
// lists and requests); no session (the domain-host bypass) gets the TS catch's answer; two first requests at once do not fail on the list's unique index
// (the second finds the first's list); the online checks whose results the TS code discards are not made.

/// <summary>An answer to one of the friends reads: always sent with 200, as the TS server does.</summary>
public sealed record FriendsPage(JsonObject Body)
{
    /// <summary>The body as the TS server's res.send writes it (JSON.stringify).</summary>
    public string Json => Js.Stringify(Body);
}

public interface IFriendsService
{
    /// <summary>GET /friends/me for <paramref name="accountId"/> (null: no session).</summary>
    Task<FriendsPage> FriendsAsync(string? accountId, CancellationToken ct = default);

    /// <summary>GET /social/me/blocked.</summary>
    Task<FriendsPage> BlockedAsync(string? accountId, CancellationToken ct = default);

    /// <summary>GET /friends/me/invitations/incoming.</summary>
    Task<FriendsPage> IncomingAsync(string? accountId, CancellationToken ct = default);

    /// <summary>GET /friends/me/invitations/outgoing.</summary>
    Task<FriendsPage> OutgoingAsync(string? accountId, CancellationToken ct = default);
}

internal sealed class FriendsService(IServiceProvider services, TimeProvider time, ILogger<FriendsService> log) : IFriendsService
{
    public const string ListsCollection = "friendlists";
    public const string RequestsCollection = "friendrequests";

    private const string AvatarImageUrl = "https://prod-network-images.wbagora.com/network/account-wbgames-com/multiversus-finn.jpg";

    /// <summary>The TS catch blocks' answer: an empty page of 1000.</summary>
    private static FriendsPage Fallback() => Page([], 1000);

    private static FriendsPage Page(JsonArray results, int pageSize) =>
        new(new JsonObject { ["total"] = results.Count, ["page"] = 1, ["page_size"] = pageSize, ["results"] = results });

    public Task<FriendsPage> FriendsAsync(string? accountId, CancellationToken ct) => Guarded("GET /friends/me", accountId, async (mongo, redis) =>
    {
        if (!ObjectId.TryParse(accountId, out var playerId))
        {
            return Page([], 20);
        }

        var list = await FindListAsync(mongo, accountId!, ct);
        var results = Entries(list, "active");
        // The TS handler makes sure the list exists whether or not the player does, then copies the blocked ids.
        list ??= await CreateListAsync(mongo, accountId!, ct);
        if (await FindPlayerAsync(mongo, playerId, ct) is { } player)
        {
            await CopyBlockedAsync(mongo, redis, player, list, ct);
        }

        return Page(results, 20);
    }, ct);

    public Task<FriendsPage> BlockedAsync(string? accountId, CancellationToken ct) => Guarded("GET /social/me/blocked", accountId, async (mongo, redis) =>
    {
        // The TS handler's findById throws on an id that is not an ObjectId: its catch answers.
        if (!ObjectId.TryParse(accountId, out var playerId))
        {
            return Fallback();
        }

        var list = await FindListAsync(mongo, accountId!, ct);
        var results = Entries(list, "blocked");
        if (await FindPlayerAsync(mongo, playerId, ct) is { } player)
        {
            list ??= await CreateListAsync(mongo, accountId!, ct);
            await CopyBlockedAsync(mongo, redis, player, list, ct);
        }

        return Page(results, 20);
    }, ct);

    public Task<FriendsPage> IncomingAsync(string? accountId, CancellationToken ct) => Guarded("GET /friends/me/invitations/incoming", accountId, async (mongo, _) =>
    {
        if (!ObjectId.TryParse(accountId, out ObjectId _))
        {
            return Page([], 1000);
        }

        var results = new JsonArray();
        foreach (var request in await PendingAsync(mongo, "toAccountId", accountId!, ct))
        {
            var account = new JsonObject();
            Put(account, "public_id", request, "fromAccountId");
            Put(account, "username", request, "fromUsername");
            results.Add(Invitation(request, account));
        }

        return Page(results, 1000);
    }, ct);

    public Task<FriendsPage> OutgoingAsync(string? accountId, CancellationToken ct) => Guarded("GET /friends/me/invitations/outgoing", accountId, async (mongo, _) =>
    {
        if (!ObjectId.TryParse(accountId, out ObjectId _))
        {
            return Page([], 1000);
        }

        var results = new JsonArray();
        foreach (var request in await PendingAsync(mongo, "fromAccountId", accountId!, ct))
        {
            var account = new JsonObject();
            Put(account, "public_id", request, "toAccountId");
            Put(account, "username", request, "toUsername");
            account["avatar"] = new JsonObject { ["name"] = "Nope", ["image_url"] = "Nope" };
            results.Add(Invitation(request, account));
        }

        return Page(results, 1000);
    }, ct);

    /// <summary>Runs a read the way the TS handlers do: no session, no store or any error answers the fallback page.</summary>
    private async Task<FriendsPage> Guarded(string route, string? accountId, Func<IMongoDatabase, IDatabase, Task<FriendsPage>> read, CancellationToken ct)
    {
        if (accountId is null)
        {
            return Fallback();
        }

        var mongo = services.GetService<IMongoDatabase>();
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        if (mongo is null || redis is null)
        {
            log.LogError("{Route} for {Account}: this service has no {Missing}", route, accountId, mongo is null ? "Mongo (MONGODB_URI)" : "Redis (REDIS)");
            return Fallback();
        }

        try
        {
            return await read(mongo, redis);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "{Route} for {Account} failed", route, accountId);
            return Fallback();
        }
    }

    private static Task<BsonDocument?> FindListAsync(IMongoDatabase mongo, string accountId, CancellationToken ct) =>
        mongo.GetCollection<BsonDocument>(ListsCollection).Find(new BsonDocument("accountId", accountId)).FirstOrDefaultAsync(ct)!;

    /// <summary>The TS ensureFriendList's new document; a list another request created first is read instead.</summary>
    private static async Task<BsonDocument> CreateListAsync(IMongoDatabase mongo, string accountId, CancellationToken ct)
    {
        var list = new BsonDocument { { "accountId", accountId }, { "friends", new BsonArray() }, { "_id", ObjectId.GenerateNewId() }, { "__v", 0 } };
        try
        {
            await mongo.GetCollection<BsonDocument>(ListsCollection).InsertOneAsync(list, cancellationToken: ct);
            return list;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await FindListAsync(mongo, accountId, ct) ?? list;
        }
    }

    private async Task<PlayerRecord?> FindPlayerAsync(IMongoDatabase mongo, ObjectId playerId, CancellationToken ct)
    {
        var doc = await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("_id", playerId)).FirstOrDefaultAsync(ct);
        return doc is null ? null : PlayerRecord.Load(doc, time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The TS ensureNoAssholes: every blocked id in the friend list that blockedPlayers lacks is appended, and then the
    /// whole array goes to Redis for the services that check blocks. Nothing is written when nothing is missing.
    /// </summary>
    private static async Task CopyBlockedAsync(IMongoDatabase mongo, IDatabase redis, PlayerRecord player, BsonDocument list, CancellationToken ct)
    {
        var blocked = (player.Get("blockedPlayers") as BsonArray ?? []).Select(JsString).ToList();
        bool added = false;
        foreach (var entry in FriendEntries(list).Where(e => Status(e) == "blocked"))
        {
            string? id = entry.TryGetValue("friendAccountId", out var v) ? JsString(v) : null;
            if (!blocked.Contains(id))
            {
                blocked.Add(id);
                player.Push("blockedPlayers", id is null ? BsonNull.Value : id);
                added = true;
            }
        }

        if (!added)
        {
            return;
        }

        await player.SaveAsync(mongo.GetCollection<BsonDocument>(PlayerRecord.Collection), ct);
        await redis.StringSetAsync($"player:{player.IdHex}:blocked", Js.Stringify(new JsonArray(blocked.Select(b => (JsonNode?)b).ToArray())));
    }

    private static IEnumerable<BsonDocument> FriendEntries(BsonDocument? list) =>
        list?.GetValue("friends", BsonNull.Value) is BsonArray friends ? friends.OfType<BsonDocument>() : [];

    // FriendEntry's status default ("active") applies to a missing field only.
    private static string? Status(BsonDocument entry) =>
        entry.TryGetValue("status", out var v) ? JsString(v) : "active";

    /// <summary>The TS getUserFriendsList: the entries of one status, in the list's order.</summary>
    private JsonArray Entries(BsonDocument? list, string status)
    {
        var results = new JsonArray();
        foreach (var entry in FriendEntries(list).Where(e => Status(e) == status))
        {
            var account = new JsonObject();
            if (entry.TryGetValue("friendAccountId", out var id))
            {
                account["public_id"] = JsString(id);
            }

            string? username = entry.TryGetValue("friendUsername", out var name) ? JsString(name) : null;
            account["username"] = string.IsNullOrEmpty(username) ? "Unknown" : username;
            account["avatar"] = new JsonObject { ["name"] = "MultiVersus", ["image_url"] = AvatarImageUrl };
            results.Add(new JsonObject { ["created_at"] = IsoTime(entry), ["account"] = account });
        }

        return results;
    }

    // f.addedAt ? new Date(f.addedAt).toISOString() : new Date().toISOString(); a missing addedAt is "now" (its default).
    private string IsoTime(BsonDocument entry)
    {
        var added = entry.GetValue("addedAt", BsonNull.Value);
        var at = added.IsValidDateTime ? added.ToUniversalTime()
            : added.IsBsonNull ? time.GetUtcNow().UtcDateTime
            : throw new FormatException($"addedAt is a {added.BsonType}");
        return at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static Task<List<BsonDocument>> PendingAsync(IMongoDatabase mongo, string field, string accountId, CancellationToken ct) =>
        mongo.GetCollection<BsonDocument>(RequestsCollection).Find(new BsonDocument { { field, accountId }, { "status", "pending" } }).ToListAsync(ct);

    private static JsonObject Invitation(BsonDocument request, JsonObject account)
    {
        var result = new JsonObject();
        Put(result, "_id", request, "_id");
        Put(result, "sent_from", request, "fromAccountId");
        Put(result, "sent_to", request, "toAccountId");
        result["state"] = "open";
        result["account"] = account;
        return result;
    }

    /// <summary>A raw document's field as JSON.stringify writes it; a missing field (undefined) is left out.</summary>
    private static void Put(JsonObject target, string key, BsonDocument source, string field)
    {
        if (source.TryGetValue(field, out var value))
        {
            target[key] = JsValue(value);
        }
    }

    private static JsonNode? JsValue(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.String => value.AsString,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.DateTime => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        _ => throw new FormatException($"a {value.BsonType} where the TS server has a plain value"),
    };

    // A String schema path as mongoose casts it on load: an ObjectId or a number becomes its string.
    private static string? JsString(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.String => value.AsString,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Boolean => value.ToString()!.ToLowerInvariant(),
        _ => throw new FormatException($"a {value.BsonType} where the schema has a string"),
    };
}
