import express, { Request, Response } from "express";
import { MVSQueries } from "./interfaces/queries_types";
import * as h from "./handlers";
//import * as ssc from "./ssc";
import { batchMiddleware } from "./middleware/batchMiddleware";
import { equip_announce_pack, equip_banner, equip_ringout_vfx, equip_stat_tracker, set_profile_icon, equip_taunt } from "./handlers/cosmetics";
import { redisClient, redisGetPlayerLobby, redisGetLobbyState, redisDeletePlayerLobby, redisDeleteLobbyState } from "./config/redis";
import { logger } from "./config/logger";
// performGenuineLeave removed — leave handled by shared.routes.ts
import { sendFriendRequest, acceptFriendRequest, declineFriendRequest, removeFriend, blockPlayer, searchPlayers, getFriends } from "./services/friendService";
import { getProfileForMatch } from "./services/profileService";
import { PlayerTesterModel } from "./database/PlayerTester";
import * as AuthUtils from "./utils/auth";
import { HydraEncoder } from "mvs-dump";
import { resolveAccountFromRequest } from "./services/identityService";
import { tryGetRealIP } from "./utils/garbagecan";
// SSC custom lobby imports removed — handled by shared.routes.ts

interface MVSParams {
  id: string;
}

const router = express.Router();

router.put("/ssc/invoke/equip_taunt", (req: Request, res: Response) => {
  equip_taunt(req, res);
});

router.put("/ssc/invoke/set_profile_icon", (req: Request, res: Response) => {
  set_profile_icon(req, res);
});

router.put("/ssc/invoke/equip_stat_tracker", (req: Request, res: Response) => {
  equip_stat_tracker(req, res);
});

router.put("/ssc/invoke/equip_announcer_pack", (req: Request, res: Response) => {
  equip_announce_pack(req, res);
});

router.put("/ssc/invoke/equip_banner", (req: Request, res: Response) => {
  equip_banner(req, res);
});

router.put("/ssc/invoke/equip_ringout_vfx", (req: Request, res: Response) => {
  equip_ringout_vfx(req, res);
});

router.post("/access", async (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  await h.handleAccess(req, res);
});

router.post("/.*/access", async (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  await h.handleAccess(req, res);
});

router.delete("/access", async (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  await h.deleteAccess(req, res);
});

router.put("/accounts/wb_network/bulk", (req: Request<{}, {}, {}, MVSQueries.Accounts_wb_network_bulk_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleAccounts_wb_network_bulk(req, res);
});

// Single account lookup — resolves sender identity after WBPNFriendRequestReceivedNotification.
// Uses a specific path pattern to avoid catching /accounts/wb_network/bulk.
router.get("/accounts/wb_network/:id", async (req: Request<{ id: string }>, res: Response) => {
  try {
    const accountId = req.params.id;
    // Skip "bulk" — that's handled by the PUT route
    if (accountId === "bulk") { res.send({}); return; }
    const player = await PlayerTesterModel.findById(accountId).catch(() => null);
    if (!player) {
      const playerByPublicId = await PlayerTesterModel.findOne({ public_id: accountId });
      if (playerByPublicId) {
        res.send({
          id: playerByPublicId._id?.toString(),
          "identity.default_username": true,
          "identity.username": playerByPublicId.name,
          presence: "online",
          presence_state: 1,
        });
        return;
      }
      res.send({});
      return;
    }
    res.send({
      id: accountId,
      "identity.default_username": true,
      "identity.username": player.name,
      presence: "online",
      presence_state: 1,
    });
  } catch (error) {
    logger.error(`[Accounts]: Error in GET /accounts/wb_network/:id: ${error}`);
    res.send({});
  }
});

router.put("/batch", batchMiddleware, async (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  await h.handleBatch(req, res);
});

router.get("/commerce/products", (req: Request<{}, {}, {}, MVSQueries.Commerce_products_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleCommerce_products(req, res);
});

router.get("/commerce/purchases/me", (req: Request<{}, {}, {}, MVSQueries.Commerce_purchases_me_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleCommerce_purchases_me(req, res);
});

router.get("/commerce/steam/mtx_user_info/me", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleCommerce_steam_mtx_user_info_me(req, res);
});

router.post(
  "/datarouter/api/v1/public/data/clients",
  (req: Request<{}, {}, {}, MVSQueries.Datarouter_api_v1_public_data_clients_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleDatarouter_api_v1_public_data_clients(req, res);
  },
);

router.put("/drives/multiversus/sync", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleDrives_multiversus_sync(req, res);
});

router.get("/file_storage", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage(req, res);
});

router.get("/file_storage/beginnermode-carousel-keyart", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_beginnermode_carousel_keyart(req, res);
});

router.get("/file_storage/beginnermode-carousel-thumbnail", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_beginnermode_carousel_thumbnail(req, res);
});

router.get("/file_storage/harley-rift-s5-keyart", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_harley_rift_s5_keyart(req, res);
});

router.get("/file_storage/harley-rift-s5-thumbnail", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_harley_rift_s5_thumbnail(req, res);
});

router.get("/file_storage/s5-bp-carousel-keyart", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_s5_bp_carousel_keyart(req, res);
});

router.get("/file_storage/s5-bp-carousel-thumbnail", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_s5_bp_carousel_thumbnail(req, res);
});

router.get("/file_storage/t-discord-qa-carousel-keyart", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_t_discord_qa_carousel_keyart(req, res);
});

router.get("/file_storage/t-discord-qa-carousel-thumbnail", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_t_discord_qa_carousel_thumbnail(req, res);
});

router.get("/file_storage/wonderwoman-arena-keyart", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_wonderwoman_arena_keyart(req, res);
});

router.get("/file_storage/wonderwoman-arena-thumbnail", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFile_storage_wonderwoman_arena_thumbnail(req, res);
});

router.get("/friends/me", (req: Request<{}, {}, {}, MVSQueries.Friends_me_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFriends_me(req, res);
});

router.get("/friends/me/invitations/incoming", (req: Request<{}, {}, {}, MVSQueries.Friends_me_invitations_incoming_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFriends_me_invitations_incoming(req, res);
});

router.get("/friends/me/invitations/outgoing", (req: Request<{}, {}, {}, MVSQueries.Friends_me_invitations_outgoing_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleFriends_me_invitations_outgoing(req, res);
});

// --- Native accept/decline friend request endpoints ---
// Game sends PUT /friends/me/invitations/:requestId/accept or /decline
router.put("/friends/me/invitations/:id/accept", async (req: Request<{ id: string }>, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;
    const requestId = req.params.id;

    logger.info(`[Friends]: Accept invitation ${requestId} by ${myId}`);
    const result = await acceptFriendRequest(requestId, myId);
    logger.info(`[Friends]: Accept result: ${JSON.stringify(result)}`);

    res.send({ status: "ok" });
  } catch (error) {
    logger.error(`[Friends]: Error accepting invitation: ${error}`);
    res.send({ status: "ok" });
  }
});

router.put("/friends/me/invitations/:id/decline", async (req: Request<{ id: string }>, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;
    const requestId = req.params.id;

    logger.info(`[Friends]: Decline invitation ${requestId} by ${myId}`);
    const result = await declineFriendRequest(requestId, myId);
    logger.info(`[Friends]: Decline result: ${JSON.stringify(result)}`);

    res.send({ status: "ok" });
  } catch (error) {
    logger.error(`[Friends]: Error declining invitation: ${error}`);
    res.send({ status: "ok" });
  }
});

// --- Native unfriend endpoint ---
// Game sends PUT /friends/me/unfriend/:publicId when removing a friend
router.put("/friends/me/unfriend/:id", async (req: Request<{ id: string }>, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;
    const targetPublicId = req.params.id;

    logger.info(`[Friends]: Unfriend request from ${myId} for public_id ${targetPublicId}`);

    // The game sends public_id (UUID), but removeFriend expects accountId (MongoDB _id).
    // Look up the player by public_id to get their _id.
    const targetPlayer = await PlayerTesterModel.findOne({ public_id: targetPublicId });
    if (targetPlayer) {
      const result = await removeFriend(myId, targetPlayer._id.toString());
      logger.info(`[Friends]: Unfriend result: ${JSON.stringify(result)}`);
    } else {
      logger.warn(`[Friends]: Could not find player with public_id ${targetPublicId}`);
    }

    res.send({ status: "ok" });
  } catch (error) {
    logger.error(`[Friends]: Error in unfriend: ${error}`);
    res.send({ status: "ok" });
  }
});

// Old DLL search handler removed — native search now works via friendsRouter in modules/friends/friends.routes.ts

// --- OVS Friend API endpoints ---
// These are called by the game's native friend UI via the DLL notification system,
// or could be called directly by the game if we hook the right SSC calls.

// --- All players endpoint (for DLL startup pre-registration) ---
router.get("/ovs/all-players", async (req: Request, res: Response) => {
  try {
    const allPlayers = await PlayerTesterModel.find({}).limit(200);
    const players = allPlayers.map(p => ({
      accountId: p.id || p._id?.toString(),
      username: p.name,
    })).filter(p => p.accountId && p.username);
    logger.info(`[AllPlayers]: returning ${players.length} players for DLL pre-registration`);
    res.json(players);
  } catch (error) {
    logger.error(`[AllPlayers]: Error: ${error}`);
    res.json([]);
  }
});

// Simple friend request from DLL search (PUT with target ID in URL, sender identified by IP)
router.put("/ovs/friends/send-request/:targetId", async (req: Request, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const targetId = req.params.targetId;
    const target = await PlayerTesterModel.findById(targetId);
    if (!target) {
      res.json({ success: false, error: "player_not_found" });
      return;
    }

    const result = await sendFriendRequest(
      connection.id,
      connection.username || connection.hydraUsername || "Unknown",
      targetId,
      target.name,
    );
    logger.info(`[FriendReq]: ${connection.username} → ${target.name}: ${result.success ? "sent" : result.error}`);
    res.json(result);
  } catch (error) {
    logger.error(`[FriendReq]: Error: ${error}`);
    res.json({ success: false, error: "server_error" });
  }
});

router.post("/ovs/friends/request", async (req: Request, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const { targetId } = req.body;
    if (!targetId) {
      res.status(400).json({ error: "targetId required" });
      return;
    }

    // Look up target player's username
    const targetConn = await redisClient.hGetAll(`connections:${targetId}`);
    const targetUsername = targetConn?.username || targetConn?.hydraUsername || "Unknown";
    const myUsername = connection.username || connection.hydraUsername || "Unknown";

    const result = await sendFriendRequest(connection.id, myUsername, targetId, targetUsername);
    res.json(result);
  } catch (error) {
    logger.error(`[Friends]: Error sending friend request: ${error}`);
    res.status(500).json({ error: "internal_error" });
  }
});

router.post("/ovs/friends/accept", async (req: Request, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const { requestId } = req.body;
    if (!requestId) {
      res.status(400).json({ error: "requestId required" });
      return;
    }

    const result = await acceptFriendRequest(requestId, connection.id);
    res.json(result);
  } catch (error) {
    logger.error(`[Friends]: Error accepting friend request: ${error}`);
    res.status(500).json({ error: "internal_error" });
  }
});

router.post("/ovs/friends/decline", async (req: Request, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const { requestId } = req.body;
    if (!requestId) {
      res.status(400).json({ error: "requestId required" });
      return;
    }

    const result = await declineFriendRequest(requestId, connection.id);
    res.json(result);
  } catch (error) {
    logger.error(`[Friends]: Error declining friend request: ${error}`);
    res.status(500).json({ error: "internal_error" });
  }
});

router.delete("/ovs/friends/:friendId", async (req: Request<{ friendId: string }>, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const result = await removeFriend(connection.id, req.params.friendId);
    res.json(result);
  } catch (error) {
    logger.error(`[Friends]: Error removing friend: ${error}`);
    res.status(500).json({ error: "internal_error" });
  }
});

router.post("/ovs/friends/block", async (req: Request, res: Response) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const connection = await resolveAccountFromRequest(req);
    if (!connection || !connection.id) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const { targetId, targetUsername } = req.body;
    if (!targetId) {
      res.status(400).json({ error: "targetId required" });
      return;
    }

    const result = await blockPlayer(connection.id, targetId, targetUsername || "Unknown");
    res.json(result);
  } catch (error) {
    logger.error(`[Friends]: Error blocking player: ${error}`);
    res.status(500).json({ error: "internal_error" });
  }
});

router.get(
  "/global_configuration_types/calendarflags/global_configurations",
  (req: Request<{}, {}, {}, MVSQueries.Global_configuration_types_calendarflags_global_configurations_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleGlobal_configuration_types_calendarflags_global_configurations(req, res);
  },
);

router.get(
  "/global_configuration_types/wwshopconfiguration/global_configurations",
  (req: Request<{}, {}, {}, MVSQueries.Global_configuration_types_wwshopconfiguration_global_configurations_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleGlobal_configuration_types_wwshopconfiguration_global_configurations(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/account-cosmetics-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_account_cosmetics_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_account_cosmetics_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/battlepass-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_battlepass_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_battlepass_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/currency-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_currency_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_currency_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/fighter-road-layout/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_fighter_road_layout_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_fighter_road_layout_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/fighter-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_fighter_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_fighter_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/main-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_main_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_main_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/prestige-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_prestige_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_prestige_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/rift-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_rift_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_rift_variant_id(req, res);
  },
);

router.get(
  "/layout/dokken-layout-type/personalized/skin-variant/:id",
  (req: Request<MVSParams, {}, {}, MVSQueries.Layout_dokken_layout_type_personalized_skin_variant_id_QUERY>, res: Response) => {
    // @ts-ignore TODO : implementation. Remove comment once implemented`
    h.handleLayout_dokken_layout_type_personalized_skin_variant_id(req, res);
  },
);

router.put("/matches/:id", (req: Request<MVSParams, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleMatches_id(req, res);
});

router.get("/matches/all/:id", (req: Request<MVSParams, {}, {}, MVSQueries.Matches_all_id_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleMatches_all_id(req, res);
});

router.post("/matches/matchmaking/1v1-retail/request", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleMatches_matchmaking_1v1_retail_request(req, res);
});

router.post("/matches/matchmaking/ranked-1v1-retail/request", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented
  h.handleMatches_matchmaking_1v1_retail_request(req, res);
});

router.post("/matches/matchmaking/2v2-retail/request", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleMatches_matchmaking_2v2_retail_request(req, res);
});

router.post("/matches/matchmaking/request/:id/cancel", async (req: Request<{ id: string }>, res: Response) => {
  await h.handle_cancel_matchmaking(req, res);
});

router.get("/objects/preferences/unique/:id/:id1", (req: Request<MVSParams, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleObjects_preferences_unique_id_id1(req, res);
});

router.put("/profiles/:id/inventory", (req: Request<MVSParams, {}, {}, MVSQueries.Profiles_id_inventory_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleProfiles_id_inventory(req, res);
});

router.put("/profiles/bulk", (req: Request<{}, {}, {}, MVSQueries.Profiles_bulk_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleProfiles_bulk(req, res);
});

// --- Individual account/profile lookup ---
// Game may fetch these after search results to get full user data.
// Without these routes, requests hit the catch-all and return SSC garbage.
router.get("/accounts/:id", async (req: Request<{ id: string }>, res: Response) => {
  try {
    // Resolve "me" to actual account ID from auth token
    const accountId = req.params.id === "me" ? req.token?.id : req.params.id;
    if (!accountId) {
      logger.warn(`[Accounts]: Cannot resolve "me" — no token`);
      res.send({});
      return;
    }
    logger.info(`[Accounts]: GET /accounts/${accountId}`);

    const player = await PlayerTesterModel.findById(accountId);
    if (!player) {
      logger.warn(`[Accounts]: Player ${accountId} not found`);
      res.status(404).send({ error: "not_found" });
      return;
    }

    res.send({
      id: accountId,
      public_id: player.public_id,
      "identity.username": player.name,
      "identity.default_username": true,
      "data.LastLoginPlatform": "EPlatform::PC",
      "server_data.ProfileIcon.Slug": player.profile_icon,
      "server_data.ProfileIcon.AssetPath": "/Game/Maps/ProfileIcons/UI_ProfileIcon_Free01.UI_ProfileIcon_Free01",
      state: "normal",
      locale: "en-US",
    });
  } catch (error) {
    logger.error(`[Accounts]: Error in GET /accounts/:id: ${error}`);
    res.send({});
  }
});

router.get("/profiles/:id", async (req: Request<{ id: string }>, res: Response) => {
  try {
    // Resolve "me" to actual account ID from auth token
    const accountId = req.params.id === "me" ? req.token?.id : req.params.id;
    if (!accountId) {
      logger.warn(`[Profiles]: Cannot resolve "me" — no token`);
      res.send({});
      return;
    }
    logger.info(`[Profiles]: GET /profiles/${accountId}`);

    const profile = await getProfileForMatch(accountId);
    if (!profile) {
      logger.warn(`[Profiles]: Profile ${accountId} not found`);
      res.status(404).send({ error: "not_found" });
      return;
    }

    res.send(profile);
  } catch (error) {
    logger.error(`[Profiles]: Error in GET /profiles/:id: ${error}`);
    res.send({});
  }
});

router.post("/sessions/auth/token", (req: Request<{}, {}, {}, MVSQueries.Sessions_auth_token_QUERY>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSessions_auth_token(req, res);
});

router.get("/social/me/blocked", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSocial_me_blocked(req, res);
});

router.post("/ssc/invoke/attempt_daily_refresh", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_attempt_daily_refresh(req, res);
});

router.post("/ssc/invoke/claim_mission_rewards", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_claim_mission_rewards(req, res);
});

router.put("/ssc/invoke/game_launch_event", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_game_launch_event(req, res);
});

router.get("/ssc/invoke/get_calendar_events", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_calendar_events(req, res);
});

router.get("/ssc/invoke/get_country_code", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_country_code(req, res);
});

router.get("/ssc/invoke/get_equipped_cosmetics", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_equipped_cosmetics(req, res);
});

router.get("/ssc/invoke/get_gm_leaderboards", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_gm_leaderboards(req, res);
});

router.get("/ssc/invoke/get_hiss_calendar_events", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_hiss_calendar_events(req, res);
});

router.get("/ssc/invoke/get_milestone_reward_tracks", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_milestone_reward_tracks(req, res);
});

router.post("/ssc/invoke/get_or_create_mission_object", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_get_or_create_mission_object(req, res);
});

router.put("/ssc/invoke/hiss_amalgamation", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_hiss_amalgamation(req, res);
});

router.get("/ssc/invoke/load_rifts", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_load_rifts(req, res);
});

router.put("/ssc/invoke/perks_lock", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_perks_lock(req, res);
});

router.get("/ssc/invoke/ranked_data", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_ranked_data(req, res);
});

// rematch_accept/decline — now handled by customLobbyRouter (SSC lobby) with fallthrough to sscRouter (web lobby)
// router.put("/ssc/invoke/rematch_decline", (req, res) => h.handleSsc_invoke_rematch_decline(req, res));
// router.put("/ssc/invoke/rematch_accept", (req, res) => h.handleSsc_invoke_rematch_accept(req, res));

router.put("/ssc/invoke/set_lobby_joinable", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_set_lobby_joinable(req, res);
});

// set_ready_for_lobby — now handled by customLobbyRouter in ssc/routes.ts
// router.put("/ssc/invoke/set_ready_for_lobby", (req: Request<{}, {}, {}, {}>, res: Response) => {
//   h.handleSsc_invoke_set_ready_for_lobby(req, res);
// });

router.put("/ssc/invoke/submit_end_of_match_stats", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_submit_end_of_match_stats(req, res);
});

router.put("/ssc/invoke/toast_player", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleSsc_invoke_toast_player(req, res);
});

router.post(/\/virtual_commerce\/purchases\/.{24}\/toasts_gleamium/, (req: Request<{}, {}, {}, {}>, res: Response) => {
  //router.post("/virtual_commerce/purchases/697fa194ce48c5be8a71abf4/toasts_gleamium", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleCommerce_purchases_toasts_gleamium(req, res);
});

router.get("/images/:image", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleImages(req, res);
});

router.get("/favicon/:image", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  // Reuse image handler for favicons since they're just images with a different path
  // This is getting its own route to avoid conflicts with the catch-all that serves SSC garbage for unknown paths
  // and also to eventually support multiple favicon sizes/versions/etc if needed.
  h.handleImages(req, res);
});

router.get("/favicon.ico", (req: Request<{}, {}, {}, {}>, res: Response) => {
  // Reuse image handler for favicons since they're just images with a different path
  // This is getting its own route to avoid conflicts with the catch-all that serves SSC garbage for unknown paths
  // and also to eventually support multiple favicon sizes/versions/etc if needed.

  req.params = { image: "favicon.ico" }; // Set the image param so the handler knows which file to serve
  // @ts-ignore TODO : implementation. Remove comment once implemented`
  h.handleImages(req, res);
});

// /ovs/leave-party removed — leave_player_lobby in shared.routes.ts handles both custom and party lobbies natively.

// DLL endpoints removed — all functionality now works server-side via WebSocket notifications.
// See DLL_REFERENCE_NOTES.md for historical RE findings.

export default router;