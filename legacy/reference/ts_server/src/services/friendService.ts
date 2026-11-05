import { logger } from "../config/logger";
import { FriendListModel, FriendEntry } from "../database/FriendList";
import { FriendRequestModel } from "../database/FriendRequest";
import { DocumentType } from "@typegoose/typegoose";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { redisGetOnlinePlayers, redisClient, RedisPlayerConnection, redisPushDLLNotification, redisGetBlockedPlayers, redisSetBlockedPlayers, DLLNotification } from "../config/redis";

const serviceName = "FriendService";
const logPrefix = `[${serviceName}]:`;

export async function ensureNoAssholes(mongoPlayer: DocumentType<PlayerTester>, accountId: string): Promise<void> {
  const blocked = await getFriends(accountId, "blocked");
    if (!mongoPlayer.blockedPlayers) {
      mongoPlayer.blockedPlayers = [];
    }

    let blockedUpdated = false;

    for (const b of blocked) {
      if (!mongoPlayer.blockedPlayers.includes(b.friendAccountId)) {
        mongoPlayer.blockedPlayers.push(b.friendAccountId);
        blockedUpdated = true;
      }
    }

    if (blockedUpdated) {
      await mongoPlayer
        .save()
        .catch(e =>
          logger.error(
            `${logPrefix} Error saving blocked players to MongoDB for account ${accountId}: ${e}`,
          ),
        );

      await redisSetBlockedPlayers(accountId, mongoPlayer.blockedPlayers);
    }
}

// Ensure a FriendList document exists for the given account
export async function ensureFriendList(accountId: string) {
  let fl = await FriendListModel.findOne({ accountId });
  if (!fl) {
    fl = await FriendListModel.create({ accountId, friends: [] });
  }
  return fl;
}

/**
 * Send a friend request from one player to another.
 * Returns { success, error?, requestId? }
 */
export async function sendFriendRequest(
  fromAccountId: string,
  fromUsername: string,
  toAccountId: string,
  toUsername: string,
): Promise<{ success: boolean; error?: string; requestId?: string }> {
  // Can't friend yourself
  if (fromAccountId === toAccountId) {
    return { success: false, error: "cannot_friend_self" };
  }

  // Check if already friends
  const fromList = await ensureFriendList(fromAccountId);
  const alreadyFriend = fromList.friends.find(
    (f) => f.friendAccountId === toAccountId && f.status === "active",
  );
  if (alreadyFriend) {
    return { success: false, error: "already_friends" };
  }

  // Check if blocked
  const blocked = fromList.friends.find(
    (f) => f.friendAccountId === toAccountId && f.status === "blocked",
  );
  if (blocked) {
    return { success: false, error: "player_blocked" };
  }

  // Check if target blocked us
  const toList = await ensureFriendList(toAccountId);
  const blockedByTarget = toList.friends.find(
    (f) => f.friendAccountId === fromAccountId && f.status === "blocked",
  );
  if (blockedByTarget) {
    return { success: false, error: "blocked_by_target" };
  }

  // Check for existing pending request in either direction
  const existingRequest = await FriendRequestModel.findOne({
    $or: [
      { fromAccountId, toAccountId, status: "pending" },
      { fromAccountId: toAccountId, toAccountId: fromAccountId, status: "pending" },
    ],
  });

  if (existingRequest) {
    // If the OTHER player already sent us a request, auto-accept it
    if (existingRequest.fromAccountId === toAccountId) {
      return await acceptFriendRequest(existingRequest._id.toString(), fromAccountId);
    }
    return { success: false, error: "request_already_pending" };
  }

  // Create the friend request
  const request = await FriendRequestModel.create({
    fromAccountId,
    fromUsername,
    toAccountId,
    toUsername,
    status: "pending",
  });

  logger.info(`${logPrefix} Friend request created: ${fromUsername} -> ${toUsername}`);

  // Push DLL notification to the target player
  const dllNotif: DLLNotification = {
    type: "friend_request",
    title: "Friend Request",
    message: `${fromUsername} wants to be your friend!`,
    data: {
      fromAccountId,
      fromUsername,
      requestId: request._id.toString(),
    },
    timestamp: Date.now(),
  };
  await redisPushDLLNotification(toAccountId, dllNotif);

  // Also send native WS notification so the game shows it in the native UI
  await redisClient.publish(
    "friend:request:ws",
    JSON.stringify({
      receiverAccountId: toAccountId,
      senderAccountId: fromAccountId,
      invitationId: request._id.toString(),
    }),
  );

  return { success: true, requestId: request._id.toString() };
}

/**
 * Accept a friend request.
 * Adds both players to each other's friend lists.
 */
export async function acceptFriendRequest(
  requestId: string,
  acceptingAccountId: string,
): Promise<{ success: boolean; error?: string }> {
  const request = await FriendRequestModel.findById(requestId);
  if (!request) {
    return { success: false, error: "request_not_found" };
  }

  if (request.status !== "pending") {
    return { success: false, error: "request_not_pending" };
  }

  // Only the recipient can accept
  if (request.toAccountId !== acceptingAccountId) {
    return { success: false, error: "not_recipient" };
  }

  // Mark request as accepted
  request.status = "accepted";
  request.updatedAt = new Date();
  await request.save();

  // Add to both friend lists
  const fromList = await ensureFriendList(request.fromAccountId);
  const toList = await ensureFriendList(request.toAccountId);

  // Add to sender's friend list (if not already there)
  if (!fromList.friends.find((f) => f.friendAccountId === request.toAccountId)) {
    fromList.friends.push({
      friendAccountId: request.toAccountId,
      friendUsername: request.toUsername,
      status: "active",
      addedAt: new Date(),
    } as FriendEntry);
    await fromList.save();
  }

  // Add to recipient's friend list (if not already there)
  if (!toList.friends.find((f) => f.friendAccountId === request.fromAccountId)) {
    toList.friends.push({
      friendAccountId: request.fromAccountId,
      friendUsername: request.fromUsername,
      status: "active",
      addedAt: new Date(),
    } as FriendEntry);
    await toList.save();
  }

  logger.info(`${logPrefix} Friend request accepted: ${request.fromUsername} <-> ${request.toUsername}`);

  // Notify the sender that their request was accepted
  const dllNotif: DLLNotification = {
    type: "friend_accepted",
    title: "Friend Request Accepted",
    message: `${request.toUsername} accepted your friend request!`,
    data: {
      friendAccountId: request.toAccountId,
      friendUsername: request.toUsername,
    },
    timestamp: Date.now(),
  };
  await redisPushDLLNotification(request.fromAccountId, dllNotif);

  return { success: true };
}

/**
 * Decline a friend request.
 */
export async function declineFriendRequest(
  requestId: string,
  decliningAccountId: string,
): Promise<{ success: boolean; error?: string }> {
  const request = await FriendRequestModel.findById(requestId);
  if (!request) {
    return { success: false, error: "request_not_found" };
  }

  if (request.status !== "pending") {
    return { success: false, error: "request_not_pending" };
  }

  if (request.toAccountId !== decliningAccountId) {
    return { success: false, error: "not_recipient" };
  }

  request.status = "declined";
  request.updatedAt = new Date();
  await request.save();

  logger.info(`${logPrefix} Friend request declined: ${request.fromUsername} -> ${request.toUsername}`);
  return { success: true };
}

/**
 * Remove a friend from both players' friend lists.
 */
export async function removeFriend(
  accountId: string,
  friendAccountId: string,
): Promise<{ success: boolean; error?: string }> {
  // Remove from my list
  await FriendListModel.updateOne(
    { accountId },
    { $pull: { friends: { friendAccountId } } },
  );

  // Remove from their list
  await FriendListModel.updateOne(
    { accountId: friendAccountId },
    { $pull: { friends: { friendAccountId: accountId } } },
  );

  const mongoPlayer = await PlayerTesterModel.findById(accountId);
  if (mongoPlayer) {
    if (!mongoPlayer.blockedPlayers)
    {
      mongoPlayer.blockedPlayers = [];
    }
    if (mongoPlayer.blockedPlayers.includes(friendAccountId)) {
      mongoPlayer.blockedPlayers = mongoPlayer.blockedPlayers.filter(id => id !== friendAccountId);
      await mongoPlayer.save();
    }

    await redisSetBlockedPlayers(accountId, mongoPlayer.blockedPlayers);
  }

  logger.info(`${logPrefix} Friend removed: ${accountId} <-> ${friendAccountId}`);
  return { success: true };
}

/**
 * Block a player. Removes friendship and prevents future requests.
 */
export async function blockPlayer(
  accountId: string,
  targetAccountId: string,
  targetUsername: string,
): Promise<{ success: boolean; error?: string }> {
  if (accountId === targetAccountId) {
    return { success: false, error: "cannot_block_self" };
  }

  const myList = await ensureFriendList(accountId);
  const mongoPlayer = await PlayerTesterModel.findById(accountId);

  // Remove any active friendship
  myList.friends = myList.friends.filter(
    (f) => f.friendAccountId !== targetAccountId,
  ) as any;

  // Add as blocked
  myList.friends.push({
    friendAccountId: targetAccountId,
    friendUsername: targetUsername,
    status: "blocked",
    addedAt: new Date(),
  } as FriendEntry);
  await myList.save();

  // Remove from their list too
  await FriendListModel.updateOne(
    { accountId: targetAccountId },
    { $pull: { friends: { friendAccountId: accountId } } },
  );

  // Cancel any pending requests between the two
  await FriendRequestModel.updateMany(
    {
      $or: [
        { fromAccountId: accountId, toAccountId: targetAccountId, status: "pending" },
        { fromAccountId: targetAccountId, toAccountId: accountId, status: "pending" },
      ],
    },
    { $set: { status: "declined", updatedAt: new Date() } },
  );

  if (mongoPlayer) {
    if (!mongoPlayer.blockedPlayers)
    {
      mongoPlayer.blockedPlayers = [];
    }
    if (!mongoPlayer.blockedPlayers.includes(targetAccountId)) {
      mongoPlayer.blockedPlayers.push(targetAccountId);
      await mongoPlayer.save();
    }

    await redisSetBlockedPlayers(accountId, mongoPlayer.blockedPlayers);
  }

  logger.info(`${logPrefix} Player blocked: ${accountId} blocked ${targetAccountId}`);
  return { success: true };
}

/**
 * Get a player's friend list with online status from Redis.
 */
export async function getFriends(accountId: string, friendType: string = "active") {
  const fl = await ensureFriendList(accountId);
  const activeFriends = fl.friends.filter((f) => f.status === friendType);

  // Get online player set from Redis
  const onlinePlayerIds = await redisGetOnlinePlayers();
  const onlineSet = new Set(onlinePlayerIds);

  // Build friend entries with online status
  const results = [];
  for (const friend of activeFriends) {
    const isOnline = onlineSet.has(friend.friendAccountId);

    // Try to get connection details from Redis for online players
    let publicId = friend.friendAccountId;
    let displayName = friend.friendUsername;

    if (isOnline) {
      const connection = (await redisClient.hGetAll(
        `connections:${friend.friendAccountId}`,
      )) as unknown as RedisPlayerConnection;
      if (connection && connection.id) {
        displayName = connection.username || connection.hydraUsername || friend.friendUsername;
        publicId = connection.public_id || friend.friendAccountId;
      }
    }

    results.push({
      friendAccountId: friend.friendAccountId,
      username: displayName,
      publicId,
      isOnline,
      addedAt: friend.addedAt,
    });
  }

  return results;
}

/**
 * Get pending incoming friend requests for a player.
 */
export async function getPendingIncomingRequests(accountId: string) {
  return await FriendRequestModel.find({
    toAccountId: accountId,
    status: "pending",
  }).sort({ createdAt: -1 });
}

/**
 * Get pending outgoing friend requests from a player.
 */
export async function getPendingOutgoingRequests(accountId: string) {
  return await FriendRequestModel.find({
    fromAccountId: accountId,
    status: "pending",
  }).sort({ createdAt: -1 });
}

/**
 * Search for players by username (partial match, case-insensitive).
 * Returns up to 20 results, excluding the searching player.
 * Empty query returns all online players (for the initial search UI).
 */
export async function searchPlayers(query: string, excludeAccountId: string) {
  let players;

  if (!query || query.length === 0) {
    // Empty query: return online players so the search UI isn't blank
    const onlinePlayerIds = await redisGetOnlinePlayers();
    const onlineIds = onlinePlayerIds.filter((id) => id !== excludeAccountId);
    if (onlineIds.length === 0) return [];
    players = await PlayerTesterModel.find({ _id: { $in: onlineIds } }).limit(20);
  } else {
    // Search PlayerTester documents by name (case-insensitive partial match)
    const escapedQuery = query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    players = await PlayerTesterModel.find({
      name: { $regex: escapedQuery, $options: "i" },
    }).limit(20);
  }

  // Get online players for status
  const onlinePlayerIds = await redisGetOnlinePlayers();
  const onlineSet = new Set(onlinePlayerIds);

  // Check which are already friends with the searcher
  const fl = await ensureFriendList(excludeAccountId);
  const friendMap = new Map(fl.friends.map((f) => [f.friendAccountId, f.status]));

  return players
    .filter((p) => p.id !== excludeAccountId)
    .map((p) => ({
      accountId: p.id,
      username: p.name,
      publicId: p.public_id,
      isOnline: onlineSet.has(p.id),
      friendStatus: friendMap.get(p.id) || "none", // "active", "blocked", or "none"
    }));
}