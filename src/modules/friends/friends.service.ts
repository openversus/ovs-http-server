import { Types } from "mongoose";
import { PlayerTesterModel } from "../../database/PlayerTester";
import { FriendListModel } from "../../database/FriendList";
import { FriendRequestModel } from "../../database/FriendRequest";
import { redisClient, redisSetBlockedPlayers } from "../../config/redis";
import { logger } from "../../config/logger";

const logPrefix = "[Friends.Service]:";

// ─── Friend List ──────────────────────────────────────────────────────────────

/**
 * Get a player's friend list as an array of account ID strings.
 * The game calls GET /friends/me, gets these IDs, then calls
 * PUT /accounts/wb_network/bulk to get detailed friend info.
 */
export async function getUserFriendsList(userId: string, friendType = "active") {
  const friendDoc = await FriendListModel.findOne({ accountId: userId });
  if (!friendDoc || !friendDoc.friends) return [];
  // 4-field format wrapped in account object — game parser extracts public_id from this.
  // Check actual online status for each friend
  const friendEntries = friendDoc.friends.filter((f) => f.status === friendType);
  return await Promise.all(
      friendEntries.map(async (f) => {
        const isOnline = await redisClient.sIsMember("online_players", f.friendAccountId);
        return {
          created_at: f.addedAt ? new Date(f.addedAt).toISOString() : new Date().toISOString(),
          account: {
            public_id: f.friendAccountId,
            username: f.friendUsername || "Unknown",
            avatar: {
              name: "MultiVersus",
              image_url: "https://prod-network-images.wbagora.com/network/account-wbgames-com/multiversus-finn.jpg",
            },
          },
        };
      }),
    );
}

// ─── Friend Details (for /accounts/wb_network/bulk and /:id) ─────────────────

/**
 * Returns detailed account info for a list of player IDs.
 * The game uses this to render friend list entries (username, presence, profile icon).
 * KEY: All IDs are unified — _id = public_id = wb_network id.
 */
export async function getUserFriendDetails(publicIds: readonly string[]) {
  const objectIds = publicIds
    .filter((id) => Types.ObjectId.isValid(id))
    .map((id) => new Types.ObjectId(id));

  const players = await PlayerTesterModel.find({ _id: { $in: objectIds } }).lean();

  // Check online status from Redis
  const presenceResults = await Promise.all(
    players.map(async (p) => {
      const isOnline = await redisClient.sIsMember("online_players", p._id.toString());
      return { id: p._id.toString(), online: isOnline };
    }),
  );

  return players.map((p) => {
    const playerId = p._id.toString();
    const presence = presenceResults.find((r) => r.id === playerId);
    const presenceState = presence?.online ? "online" : "offline";
    const username = p.name || p.hydraUsername || "Unknown";

    // Full Hydra account format with FLAT dot-separated keys
    // This format matches the old static accounts.ts and shows multiple friends!
    return {
      updated_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
      created_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
      deleted: false,
      orphaned: false,
      orphaned_reason: null,
      public_id: playerId,
      "identity.avatar": "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
      "identity.default_username": true,
      "identity.alternate.wb_network": [{ id: playerId, username, avatar: null }],
      "identity.alternate.steam": [{ id: playerId, username, avatar: null }],
      "wb_account.completed": true,
      "wb_account.email_verified": true,
      points: 0,
      state: "normal",
      wbplay_data_synced: false,
      wbplay_identity: null,
      locale: "en-US",
      "data.LastLoginPlatform": "EPlatform::PC",
      "server_data.ProfileIcon.Slug": p.profile_icon || "profile_icon_default",
      "server_data.ProfileIcon.AssetPath": "/Game/Panda_Main/Blueprints/Rewards/ProfileIcons/ProfileIcon_Default.ProfileIcon_Default",
      "server_data.CurrentXP": 100,
      "server_data.Level": 5,
      id: playerId,
      "identity.username": username,
      connections: [],
      presence_state: presence?.online ? 0 : 1,
      presence: presenceState,
    };
  });
}

// ─── Invitations ──────────────────────────────────────────────────────────────

export async function getIncomingInvitations(accountId: string) {
  const requests = await FriendRequestModel.find({
    toAccountId: accountId,
    status: "pending",
  }).lean();

  const results = requests.map((req) => ({
    _id: req._id,
    sent_from: req.fromAccountId,
    sent_to: req.toAccountId,
    state: "open",
    account: {
      public_id: req.fromAccountId,
      username: req.fromUsername,
    },
  }));

  return {
    total: results.length,
    page: 1,
    page_size: 1000,
    results,
  };
}

export async function getOutgoingInvitations(accountId: string) {
  const requests = await FriendRequestModel.find({
    fromAccountId: accountId,
    status: "pending",
  }).lean();

  const results = requests.map((req) => ({
    _id: req._id,
    sent_from: req.fromAccountId,
    sent_to: req.toAccountId,
    state: "open",
    account: {
      public_id: req.toAccountId,
      username: req.toUsername,
      avatar: { name: "Nope", image_url: "Nope" },
    },
  }));

  return {
    total: results.length,
    page: 1,
    page_size: 1000,
    results,
  };
}

export async function generateInvitation(senderId: string, receiverPublicId: string) {
  // Look up both players
  const sender = await PlayerTesterModel.findById(senderId).lean();
  const receiver = await PlayerTesterModel.findById(receiverPublicId).lean();

  if (!receiver) {
    throw new Error("Receiver player not found");
  }

  const senderUsername = sender?.name || sender?.hydraUsername || "Unknown";
  const receiverUsername = receiver?.name || (receiver as any)?.hydraUsername || "Unknown";
  const receiverId = receiver._id.toString();

  // Check if request already exists
  const existing = await FriendRequestModel.findOne({
    fromAccountId: senderId,
    toAccountId: receiverId,
    status: "pending",
  });
  if (existing) {
    return {
      id: existing._id,
      sent_from: senderId,
      sent_to: receiverId,
      state: "open",
      account: { public_id: receiverId, username: receiverUsername },
    };
  }

  // Create the invitation
  const invitation = await FriendRequestModel.create({
    fromAccountId: senderId,
    fromUsername: senderUsername,
    toAccountId: receiverId,
    toUsername: receiverUsername,
    status: "pending",
  });

  return {
    id: invitation._id,
    sent_from: senderId,
    sent_to: receiverId,
    state: "open",
    account: { public_id: receiverId, username: receiverUsername },
  };
}

// ─── Search ───────────────────────────────────────────────────────────────────

export async function searchProfiles(query: string) {
  const accounts = await PlayerTesterModel.find(
    { name: { $regex: query, $options: "i" } },
  ).limit(25).lean();

  // Check online status
  const presenceResults = await Promise.all(
    accounts.map(async (p) => {
      const isOnline = await redisClient.sIsMember("online_players", p._id.toString());
      return { id: p._id.toString(), online: isOnline };
    }),
  );

  const profiles = accounts.map((account) => {
    const accountId = account._id.toString();
    const presenceState = presenceResults.find((r) => r.id === accountId)?.online
      ? "online"
      : "offline";
    const username = account.name || account.hydraUsername || "Unknown";

    return {
      score: null,
      result: {
        id: accountId,
        account_id: accountId,
        updated_at: new Date(),
        created_at: new Date(),
        account: {
          deleted: false,
          orphaned: false,
          orphaned_reason: null,
          public_id: accountId,
          identity: {
            default_username: true,
            username,
            alternate: {
              wb_network: [{ id: accountId, username, avatar: null }],
            },
          },
          state: "normal",
          wbplay_data_synced: false,
          wbplay_identity: null,
          locale: "en-US",
          data: { LastLoginPlatform: "EPlatform::PC" },
          id: accountId,
          presence: presenceState,
          server_data: {
            ProfileIcon: {
              Slug: account.profile_icon || "profile_icon_default",
              AssetPath: "",
            },
          },
        },
      },
    };
  });

  return {
    cursor: null,
    start: 0,
    count: profiles.length,
    total: profiles.length,
    results: profiles,
  };
}

// ─── Profile Bulk (for /profiles/bulk) ────────────────────────────────────────

export async function getProfileBulk(userIds: string[]) {
  const objectIds = userIds
    .filter((id) => Types.ObjectId.isValid(id))
    .map((id) => new Types.ObjectId(id));

  const profiles = await PlayerTesterModel.find({ _id: { $in: objectIds } }).lean();

  return profiles.map((profile) => {
    const playerId = profile._id.toString();
    const username = profile.name || profile.hydraUsername || "Unknown";

    return {
      id: playerId,
      account_id: playerId,
      updated_at: new Date(),
      created_at: new Date(),
      account: {
        deleted: false,
        orphaned: false,
        orphaned_reason: null,
        public_id: playerId,
        identity: {
          default_username: true,
          username,
        },
        state: "normal",
        wbplay_data_synced: false,
        wbplay_identity: null,
        locale: "en-US",
        data: { LastLoginPlatform: "EPlatform::PC" },
        id: playerId,
        server_data: {
          ProfileIcon: {
            Slug: profile.profile_icon || "profile_icon_default",
            AssetPath: "",
          },
        },
      },
    };
  });
}

// export async function addBlockedPlayer(accountId: string, blockId: string) {
//   const fromList = await ensureFriendList(accountId);

//   // Add to block list (if not already there)
//   if (!fromList.friends.find((f) => f.friendAccountId === blockId)) {
//     let blockedPlayerUsername = await PlayerTesterModel.findById(blockId).lean().then((p) => p?.name || p?.hydraUsername || "Unknown");
//     fromList.friends.push({
//       friendAccountId: blockId,
//       friendUsername: blockedPlayerUsername,
//       status: "blocked",
//       addedAt: new Date(),
//     } as FriendEntry);
//     await fromList.save();
//   }

//   // let mongoPlayer = await PlayerTesterModel.findOne({ id: accountId });
//   // if (!mongoPlayer)
//   // {
//   //   return;
//   // }

//   // mongoPlayer.blockedPlayers = mongoPlayer.blockedPlayers || [];
//   // if (!mongoPlayer.blockedPlayers.includes(blockId)) {
//   //   mongoPlayer.blockedPlayers.push(blockId);
//   //   await mongoPlayer.save();
//   // }

//   // let blockDoc = await FriendListModel.findOne({ accountId });
//   // if (!blockDoc) {
//   //   blockDoc = await FriendListModel.create({ accountId, friends: [] });
//   // }
//   // let blockedPlayerUsername = await PlayerTesterModel.findById(blockId).lean().then((p) => p?.name || p?.hydraUsername || "Unknown");

//   // const existingEntry = blockDoc.friends.find((f) => f.friendAccountId === blockId);
//   // if (existingEntry) {
//   //   existingEntry.status = "blocked";
//   // } else {
//   //   blockDoc.friends.push({
//   //     friendAccountId: blockId,
//   //     friendUsername: blockedPlayerUsername,
//   //     status: "blocked",
//   //     addedAt: new Date(),
//   //   });
//   // }

//   // await blockDoc.save();
// }

// export async function removeBlockedPlayer(accountId: string, blockId: string) {
//   let mongoPlayer = await PlayerTesterModel.findOne({ id: accountId });
//   if (!mongoPlayer || !mongoPlayer.blockedPlayers) {
//     return;
//   }

//   mongoPlayer.blockedPlayers = mongoPlayer.blockedPlayers.filter((id) => id !== blockId);
//   await mongoPlayer.save();

//   let blockDoc = await FriendListModel.findOne({ accountId });
//   if (!blockDoc) {
//     return;
//   }

//   blockDoc.friends = blockDoc.friends.filter((f) => f.friendAccountId !== blockId);
//   await blockDoc.save();
// }
