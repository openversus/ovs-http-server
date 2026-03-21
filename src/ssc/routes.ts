import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import {
  handle_ssc_set_lobby_mode,
  handleSsc_invoke_create_party_lobby,
  handleSsc_invoke_create_party,
  handleSsc_invoke_perks_get_all_pages,
  perks_set_page,
  set_lock_lobby_loadout,
  set_perks_absent,
  handle_ssc_update_player_preferences,
  handleSsc_invoke_invite_to_player_lobby,
  handleSsc_invoke_leave_player_lobby,
  handleSsc_invoke_join_party_lobby,
} from "./ssc";
import * as AuthUtils from "../utils/auth";
import { sendFriendRequest, removeFriend } from "../services/friendService";
import { redisClient } from "../config/redis";
import { sharedLobbyRouter } from "../modules/lobby/shared.routes";
import { customLobbyRouter } from "../modules/customLobby/lobby.routes";
import { handleRematchAccept, handleRematchDecline } from "../services/customLobbyService";

export const sscRouter = express.Router();

// Mount shared lobby routes FIRST (handles multi-path endpoints: leave, invite, ready, loadout, rematch)
sscRouter.use(sharedLobbyRouter);
// Then custom lobby routes (handles custom-game-specific endpoints)
sscRouter.use(customLobbyRouter);

// --- Existing handlers (party lobby, perks, loadout, friend system) ---

sscRouter.put("/ssc/invoke/game_install", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received game_install request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/lock_lobby_loadout", (req: Request, res: Response) => {
  logger.info(req.body);
  set_lock_lobby_loadout(req, res);
});

sscRouter.put("/ssc/invoke/perks_absent", (req: Request, res: Response) => {
  set_perks_absent(req, res);
});

sscRouter.put("/ssc/invoke/perks_set_character_page", (req: Request, res: Response) => {
  perks_set_page(req, res);
});

sscRouter.get("/ssc/invoke/perks_get_all_pages", async (req: Request, res: Response) => {
  await handleSsc_invoke_perks_get_all_pages(req, res);
});

// PartyManager::CreateParty() calls this — SEPARATE from create_party_lobby.
// PartyManager expects flat { MatchID } response (not nested in body.lobby).
sscRouter.put("/ssc/invoke/create_party", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party(req, res);
});

sscRouter.put("/ssc/invoke/create_party_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party_lobby(req, res);
});

sscRouter.put("/ssc/invoke/set_mode_for_lobby", async (req: Request, res: Response) => {
  await handle_ssc_set_lobby_mode(req, res);
});

sscRouter.put("/ssc/invoke/update_player_preferences", async (req: Request, res: Response) => {
  await handle_ssc_update_player_preferences(req, res);
});

sscRouter.put("/ssc/invoke/invite_to_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_invite_to_player_lobby(req, res);
});

sscRouter.put("/ssc/invoke/leave_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_leave_player_lobby(req, res);
});

sscRouter.put("/ssc/invoke/join_party_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_join_party_lobby(req, res);
});

// Rematch fallback (web-based custom lobby) — reached when customLobbyRouter's next() falls through
sscRouter.put("/ssc/invoke/rematch_accept", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      logger.info("[SSC.Routes]: Web lobby rematch accept from " + playerId);
      await handleRematchAccept(playerId);
    }
    res.send({ body: [], metadata: null, return_code: 0 });
  } catch (e) {
    logger.error("[SSC.Routes]: rematch_accept error: " + e);
    res.send({ body: [], metadata: null, return_code: 0 });
  }
});

sscRouter.put("/ssc/invoke/rematch_decline", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      logger.info("[SSC.Routes]: Web lobby rematch decline from " + playerId);
      const result = await handleRematchDecline(playerId);
      if (result.playerIds && result.playerIds.length > 0) {
        await redisClient.publish("custom_lobby_rematch_decline", JSON.stringify({ playerIds: result.playerIds }));
      }
    }
    res.send({ body: [], metadata: null, return_code: 0 });
  } catch (e) {
    logger.error("[SSC.Routes]: rematch_decline error: " + e);
    res.send({ body: [], metadata: null, return_code: 0 });
  }
});

// Stub handlers for party-related SSC endpoints
sscRouter.put("/ssc/invoke/cancel_party_invite", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received cancel_party_invite request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/decline_party_invite", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received decline_party_invite request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/autoparty_join", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received autoparty_join request");
  logger.info(req.body);
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// set_lobby_joinable handled below with set_lobby_not_joinable

sscRouter.put("/ssc/invoke/set_lobby_not_joinable", async (req: Request, res: Response) => {
  // When matchmaking starts, clear pending invites so stale joins fail
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobbyId = req.body?.LobbyId;
    if (lobbyId) {
      // Mark lobby as not joinable by setting a flag
      const raw = await redisClient.get(`lobby:${lobbyId}`);
      if (raw) {
        const state = JSON.parse(raw);
        state.joinable = false;
        await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(state), { EX: 3600 });
      }
      logger.info(`[SSC.Routes]: set_lobby_not_joinable: Lobby ${lobbyId} marked not joinable`);
    }
  } catch {}
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/set_lobby_joinable", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobbyId = req.body?.LobbyId;
    if (lobbyId) {
      const raw = await redisClient.get(`lobby:${lobbyId}`);
      if (raw) {
        const state = JSON.parse(raw);
        state.joinable = true;
        await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(state), { EX: 3600 });
      }
      logger.info(`[SSC.Routes]: set_lobby_joinable: Lobby ${lobbyId} marked joinable`);
    }
  } catch {}
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/update_party_game_modes", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received update_party_game_modes request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// --- Friend system: send_profile_notification ---
// The game calls this SSC when the player uses the native friend UI to:
//   - Send a friend request (Type: "friend_request")
//   - Remove a friend (Type: "unfriend")
// Body typically contains: { TargetAccountId, Type, ... }
sscRouter.put("/ssc/invoke/send_profile_notification", async (req: Request, res: Response) => {
  try {
    const body = req.body;
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;

    logger.info(`[SSC.Routes]: send_profile_notification from ${myId}: ${JSON.stringify(body)}`);

    const targetAccountId = body.TargetAccountId || body.targetAccountId || body.target_account_id || body.AccountId || body.accountId;
    const notifType = body.Type || body.type || body.template_id || "";

    if (!targetAccountId) {
      logger.warn("[SSC.Routes]: send_profile_notification missing TargetAccountId");
      res.send({ body: {}, metadata: null, return_code: 200 });
      return;
    }

    // Get player usernames from Redis connections
    const myConn = await redisClient.hGetAll(`connections:${myId}`);
    const targetConn = await redisClient.hGetAll(`connections:${targetAccountId}`);
    const myUsername = myConn?.username || myConn?.hydraUsername || "Unknown";
    const targetUsername = targetConn?.username || targetConn?.hydraUsername || "Unknown";

    if (notifType.toLowerCase().includes("unfriend") || notifType.toLowerCase().includes("remove")) {
      // Remove friend — check BEFORE "friend" since "unfriend" contains "friend"
      const result = await removeFriend(myId, targetAccountId);
      logger.info(`[SSC.Routes]: Remove friend result: ${JSON.stringify(result)}`);
    } else if (notifType.toLowerCase().includes("friend") || notifType.toLowerCase().includes("request")) {
      // Friend request
      const result = await sendFriendRequest(myId, myUsername, targetAccountId, targetUsername);
      logger.info(`[SSC.Routes]: Friend request result: ${JSON.stringify(result)}`);
    } else {
      // Unknown notification type — log it
      logger.info(`[SSC.Routes]: Unknown profile notification type "${notifType}", ignoring`);
    }

    res.send({ body: {}, metadata: null, return_code: 200 });
  } catch (error) {
    logger.error(`[SSC.Routes]: Error in send_profile_notification: ${error}`);
    res.send({ body: {}, metadata: null, return_code: 200 });
  }
});