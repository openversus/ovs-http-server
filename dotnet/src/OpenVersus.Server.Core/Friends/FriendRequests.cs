using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Friends;

// The friend requests the game and the OpenVersus client send, ported from the TS server (services/friendService.ts:
// sendFriendRequest, acceptFriendRequest, removeFriend; modules/friends/friends.service.ts: generateInvitation; branch
// infinity-war). The native notification the TS websocket built from friend:request:ws is sent through ws:send, which
// the TS websocket and the realtime gateway both deliver. The rest of the friends writes (accept, decline, block, ...)
// are still the TS server's, on the same collections, so documents are written as mongoose writes them;
// tools/friends/friends_diff.mjs compares the two servers.
//
// Mongo, written
//   friendrequests  a new {fromAccountId, fromUsername, toAccountId, toUsername, status: "pending", createdAt, updatedAt,
//                   _id, __v: 0}; one accepted: $set {status: "accepted", updatedAt}
//   friendlists     a missing list made as FriendsService makes it; an accepted request's entry {friendAccountId,
//                   friendUsername, status: "active", addedAt}: $push $each + $inc __v on each list that has no entry
//                   for the other player (of any status); unfriend: $pull {friends: {friendAccountId}} on both lists
//   playertesters   unfriend: blockedPlayers without the other player, when it held them
// Redis, written    dll_notifications:{player} (friend_request, friend_accepted); unfriend: player:{id}:blocked, whenever
//                   the player exists
// Sent (ws:send)    WBPNFriendRequestReceivedNotification to the receiver of a new request

/// <summary>What sendFriendRequest and acceptFriendRequest answer: {success, error} or {success, requestId}.</summary>
public sealed record FriendRequestResult(bool Success, string? Error = null, string? RequestId = null)
{
    public JsonObject Json()
    {
        var json = new JsonObject { ["success"] = Success };
        if (Error is not null)
        {
            json["error"] = Error;
        }

        if (RequestId is not null)
        {
            json["requestId"] = RequestId;
        }

        return json;
    }
}

public interface IFriendRequests
{
    /// <summary>
    /// The TS sendFriendRequest: a request from one player to another, or the other's pending request to them accepted.
    /// Throws where the TS code threw (a request mongoose would not validate, Mongo or Redis failing).
    /// </summary>
    Task<FriendRequestResult> SendAsync(string fromId, string fromName, string toId, string toName, CancellationToken ct = default);

    /// <summary>
    /// POST /friends/me/invitations: the TS generateInvitation's answer, after the receiver is notified (every time, an
    /// existing request included, as there). Throws where the TS route answered 400: a sender or receiver id that is not
    /// an ObjectId, no such receiver.
    /// </summary>
    Task<JsonObject> InviteAsync(string senderId, string? senderUsername, string receiverId, CancellationToken ct = default);

    /// <summary>The TS removeFriend: the two players out of each other's lists, and out of the remover's blocked players.</summary>
    Task RemoveAsync(string accountId, string friendId, CancellationToken ct = default);

    /// <summary>A player's name (playertesters name), for PUT /ovs/friends/send-request; null when there is no such player.</summary>
    Task<PlayerName?> PlayerNameAsync(string playerId, CancellationToken ct = default);
}

/// <summary>A player found by id, and their name (null when the document has none).</summary>
public sealed record PlayerName(string? Name);

internal sealed class FriendRequests(IServiceProvider services, TimeProvider time, ILogger<FriendRequests> log) : IFriendRequests
{
    private IMongoDatabase Mongo => services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");

    private IDatabase Redis => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    private IMongoCollection<BsonDocument> Lists => Mongo.GetCollection<BsonDocument>(FriendsService.ListsCollection);

    private IMongoCollection<BsonDocument> Requests => Mongo.GetCollection<BsonDocument>(FriendsService.RequestsCollection);

    public async Task<FriendRequestResult> SendAsync(string fromId, string fromName, string toId, string toName, CancellationToken ct)
    {
        if (fromId == toId)
        {
            return new(false, "cannot_friend_self");
        }

        var fromList = await EnsureListAsync(fromId, ct);
        if (Entries(fromList).Any(e => Str(e, "friendAccountId") == toId && Status(e) == "active"))
        {
            return new(false, "already_friends");
        }

        if (Entries(fromList).Any(e => Str(e, "friendAccountId") == toId && Status(e) == "blocked"))
        {
            return new(false, "player_blocked");
        }

        var toList = await EnsureListAsync(toId, ct);
        if (Entries(toList).Any(e => Str(e, "friendAccountId") == fromId && Status(e) == "blocked"))
        {
            return new(false, "blocked_by_target");
        }

        var existing = await Requests.Find(new BsonDocument("$or", new BsonArray
        {
            new BsonDocument { { "fromAccountId", fromId }, { "toAccountId", toId }, { "status", "pending" } },
            new BsonDocument { { "fromAccountId", toId }, { "toAccountId", fromId }, { "status", "pending" } },
        })).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            // The other player already asked: theirs is accepted, and nobody is told of a new request.
            return Str(existing, "fromAccountId") == toId ? await AcceptAsync(existing, fromId, ct) : new(false, "request_already_pending");
        }

        var request = await CreateAsync(fromId, fromName, toId, toName, ct);
        string requestId = request["_id"].AsObjectId.ToString();
        log.LogInformation("Friend request created: {From} -> {To}", fromName, toName);

        var redis = Redis;
        await PlayerMessages.NotifyClientAsync(redis, toId, "friend_request", "Friend Request", $"{fromName} wants to be your friend!", new JsonObject
        {
            ["fromAccountId"] = fromId,
            ["fromUsername"] = fromName,
            ["requestId"] = requestId,
        }, time.GetUtcNow().ToUnixTimeMilliseconds());
        await PlayerMessages.SendAsync(redis, [toId], Received(toId, fromId, requestId));
        return new(true, RequestId: requestId);
    }

    public async Task<JsonObject> InviteAsync(string senderId, string? senderUsername, string receiverId, CancellationToken ct)
    {
        // findById(...).lean() of both: an id that is not an ObjectId throws (mongoose's CastError).
        var players = Mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);
        var sender = await players.Find(new BsonDocument("_id", ObjectId.Parse(senderId))).FirstOrDefaultAsync(ct);
        var receiver = await players.Find(new BsonDocument("_id", ObjectId.Parse(receiverId))).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Receiver player not found");

        string senderName = Or(Str(sender, "name"), Str(sender, "hydraUsername"), "Unknown");
        string receiverName = Or(Str(receiver, "name"), Str(receiver, "hydraUsername"), "Unknown");
        string receiverOid = receiver["_id"].AsObjectId.ToString();

        var request = await Requests.Find(new BsonDocument { { "fromAccountId", senderId }, { "toAccountId", receiverOid }, { "status", "pending" } }).FirstOrDefaultAsync(ct)
            ?? await CreateAsync(senderId, senderName, receiverOid, receiverName, ct);
        string requestId = request["_id"].AsObjectId.ToString();

        // The route's own notices, sent whether the request is new or not: the native one, then the client's.
        var redis = Redis;
        await PlayerMessages.SendAsync(redis, [receiverId], Received(receiverId, senderId, requestId));
        await PlayerMessages.NotifyClientAsync(redis, receiverId, "friend_request", "Friend Request", $"{Or(senderUsername, "Someone")} sent you a friend request!", new JsonObject
        {
            ["senderId"] = senderId,
            ["senderUsername"] = Or(senderUsername, "Unknown"),
        }, time.GetUtcNow().ToUnixTimeMilliseconds());

        return new JsonObject
        {
            ["id"] = requestId,
            ["sent_from"] = senderId,
            ["sent_to"] = receiverOid,
            ["state"] = "open",
            ["account"] = new JsonObject { ["public_id"] = receiverOid, ["username"] = receiverName },
        };
    }

    public async Task RemoveAsync(string accountId, string friendId, CancellationToken ct)
    {
        await Lists.UpdateOneAsync(new BsonDocument("accountId", accountId),
            new BsonDocument("$pull", new BsonDocument("friends", new BsonDocument("friendAccountId", friendId))), cancellationToken: ct);
        await Lists.UpdateOneAsync(new BsonDocument("accountId", friendId),
            new BsonDocument("$pull", new BsonDocument("friends", new BsonDocument("friendAccountId", accountId))), cancellationToken: ct);

        var players = Mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);
        if (await players.Find(new BsonDocument("_id", ObjectId.Parse(accountId))).FirstOrDefaultAsync(ct) is { } doc)
        {
            var player = PlayerRecord.Load(doc, time.GetUtcNow().UtcDateTime);
            var blocked = player.Get("blockedPlayers") as BsonArray ?? [];
            if (blocked.Any(b => b.IsString && b.AsString == friendId))
            {
                player.SetArray("blockedPlayers", new BsonArray(blocked.Where(b => !(b.IsString && b.AsString == friendId))));
                await player.SaveAsync(players, ct);
            }

            var now = player.Get("blockedPlayers") as BsonArray ?? [];
            await Redis.StringSetAsync($"player:{accountId}:blocked", Js.Stringify(new JsonArray([.. now.Select(b => (JsonNode?)(b.IsString ? b.AsString : null))])));
        }

        log.LogInformation("Friend removed: {Account} <-> {Friend}", accountId, friendId);
    }

    public async Task<PlayerName?> PlayerNameAsync(string playerId, CancellationToken ct)
    {
        var doc = await Mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("_id", ObjectId.Parse(playerId))).FirstOrDefaultAsync(ct);
        return doc is null ? null : new PlayerName(PlayerRecord.Load(doc, time.GetUtcNow().UtcDateTime).Str("name"));
    }

    // The TS acceptFriendRequest for a request just found pending, accepted by its receiver.
    private async Task<FriendRequestResult> AcceptAsync(BsonDocument request, string acceptingId, CancellationToken ct)
    {
        if (Str(request, "toAccountId") != acceptingId)
        {
            return new(false, "not_recipient");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await Requests.UpdateOneAsync(new BsonDocument("_id", request["_id"]),
            new BsonDocument("$set", new BsonDocument { { "status", "accepted" }, { "updatedAt", now } }), cancellationToken: ct);

        string fromId = Str(request, "fromAccountId")!, toId = acceptingId;
        string? fromName = Str(request, "fromUsername"), toName = Str(request, "toUsername");
        // Both lists made or found first, then each one's entry, as TS orders them.
        var fromList = await EnsureListAsync(fromId, ct);
        var toList = await EnsureListAsync(toId, ct);
        foreach (var (list, friendId, friendName) in new[] { (fromList, toId, toName), (toList, fromId, fromName) })
        {
            if (!Entries(list).Any(e => Str(e, "friendAccountId") == friendId))
            {
                var entry = new BsonDocument { { "friendAccountId", friendId }, { "friendUsername", friendName is null ? BsonNull.Value : friendName }, { "status", "active" }, { "addedAt", time.GetUtcNow().UtcDateTime } };
                await Lists.UpdateOneAsync(new BsonDocument("_id", list["_id"]), new BsonDocument
                {
                    { "$push", new BsonDocument("friends", new BsonDocument("$each", new BsonArray { entry })) },
                    { "$inc", new BsonDocument("__v", 1) },
                }, cancellationToken: ct);
            }
        }

        log.LogInformation("Friend request accepted: {From} <-> {To}", fromName, toName);
        await PlayerMessages.NotifyClientAsync(Redis, fromId, "friend_accepted", "Friend Request Accepted", $"{toName} accepted your friend request!", new JsonObject
        {
            ["friendAccountId"] = toId,
            ["friendUsername"] = toName,
        }, time.GetUtcNow().ToUnixTimeMilliseconds());
        return new(true);
    }

    // FriendRequestModel.create: the schema's fields in order with their defaults, then _id and __v. Every name is a
    // required String: mongoose refuses an empty or missing one (the TS routes' catch answers then).
    private async Task<BsonDocument> CreateAsync(string fromId, string fromName, string toId, string toName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toId) || string.IsNullOrEmpty(toName))
        {
            throw new InvalidOperationException("FriendRequest validation failed: a required field is empty");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var request = new BsonDocument
        {
            { "fromAccountId", fromId },
            { "fromUsername", fromName },
            { "toAccountId", toId },
            { "toUsername", toName },
            { "status", "pending" },
            { "createdAt", now },
            { "updatedAt", now },
            { "_id", ObjectId.GenerateNewId() },
            { "__v", 0 },
        };
        await Requests.InsertOneAsync(request, cancellationToken: ct);
        return request;
    }

    // ensureFriendList: the player's list, made when missing (FriendsService's document).
    private async Task<BsonDocument> EnsureListAsync(string accountId, CancellationToken ct)
    {
        if (await Lists.Find(new BsonDocument("accountId", accountId)).FirstOrDefaultAsync(ct) is { } list)
        {
            return list;
        }

        list = new BsonDocument { { "accountId", accountId }, { "friends", new BsonArray() }, { "_id", ObjectId.GenerateNewId() }, { "__v", 0 } };
        try
        {
            await Lists.InsertOneAsync(list, cancellationToken: ct);
            return list;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await Lists.Find(new BsonDocument("accountId", accountId)).FirstOrDefaultAsync(ct) ?? list;
        }
    }

    /// <summary>What the TS websocket sent a request's receiver for friend:request:ws.</summary>
    internal static JsonObject Received(string receiverId, string senderId, string invitationId) => new()
    {
        ["data"] = new JsonObject
        {
            ["template_id"] = "WBPNFriendRequestReceivedNotification",
            ["SenderWBPNAccountID"] = senderId,
            ["WBPNInvitationID"] = invitationId,
        },
        ["payload"] = new JsonObject
        {
            ["match"] = new JsonObject { ["id"] = receiverId },
            ["custom_notification"] = "realtime",
        },
        ["header"] = "",
        ["cmd"] = "profile-notification",
    };

    private static IEnumerable<BsonDocument> Entries(BsonDocument list) =>
        list.GetValue("friends", BsonNull.Value) is BsonArray friends ? friends.OfType<BsonDocument>() : [];

    // FriendEntry's status default ("active") applies to a missing field only (a mongoose document, not lean).
    private static string? Status(BsonDocument entry) => entry.TryGetValue("status", out var v) ? (v.IsString ? v.AsString : null) : "active";

    private static string? Str(BsonDocument? doc, string field) =>
        doc is not null && doc.TryGetValue(field, out var v) ? v.BsonType switch
        {
            BsonType.String => v.AsString,
            BsonType.ObjectId => v.AsObjectId.ToString(),
            _ => null,
        } : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
