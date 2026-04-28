import express, { Request, Response } from "express";
import { logger } from "../../config/logger";
import { redisClient, FRIEND_REQUEST_WS_CHANNEL, redisPushDLLNotification, DLLNotification, redisSetBlockedPlayers } from "../../config/redis";
import { blockPlayer, ensureNoAssholes, getFriends, removeFriend } from "../../services/friendService";
import { PlayerTester, PlayerTesterModel } from "../../database/PlayerTester";
import { HydraEncoder } from "mvs-dump";
import * as AuthUtils from "../../utils/auth";
import {
  getUserFriendsList,
  getUserFriendDetails,
  getIncomingInvitations,
  getOutgoingInvitations,
  generateInvitation,
  searchProfiles,
  getProfileBulk
} from "./friends.service";

const logPrefix = "[Friends.Routes]:";

export const friendsRouter = express.Router();

// ─── GET /friends/me ─────────────────────────────────────────────────────────
// Returns friend IDs. Game then calls /accounts/wb_network/bulk for details.
friendsRouter.get("/friends/me", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const fContentType = req.headers["content-type"] || "none";
    const friends = await getUserFriendsList(account.id, "active");

    const mongoPlayer = await PlayerTesterModel.findOne({ id: account.id });
    const blocked = await getFriends(account.id, "blocked");

    if (mongoPlayer) {
      await ensureNoAssholes(mongoPlayer, account.id);
    }

    logger.info(`${logPrefix} GET /friends/me — returning ${friends.length} friends for ${account.id} (content-type: ${fContentType})`);
    res.send({
      total: friends.length,
      page: 1,
      page_size: 20,
      results: friends,
    });
  } catch (e) {
    logger.error(`${logPrefix} GET /friends/me error: ${e}`);
    res.send({ total: 0, page: 1, page_size: 1000, results: [] });
  }
});

// ─── GET /friends/me/invitations/incoming ────────────────────────────────────
friendsRouter.get("/friends/me/invitations/incoming", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const result = await getIncomingInvitations(account.id);
    logger.info(`${logPrefix} Returning ${result.total} incoming invitations for ${account.id}`);
    res.send(result);
  } catch (e) {
    logger.error(`${logPrefix} incoming invitations error: ${e}`);
    res.send({ total: 0, page: 1, page_size: 1000, results: [] });
  }
});

// ─── GET /friends/me/invitations/outgoing ────────────────────────────────────
friendsRouter.get("/friends/me/invitations/outgoing", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const result = await getOutgoingInvitations(account.id);
    logger.info(`${logPrefix} Returning ${result.total} outgoing invitations for ${account.id}`);
    res.send(result);
  } catch (e) {
    logger.error(`${logPrefix} outgoing invitations error: ${e}`);
    res.send({ total: 0, page: 1, page_size: 1000, results: [] });
  }
});

// ─── POST /friends/me/invitations ────────────────────────────────────────────
// Game sends friend request via native UI
friendsRouter.post("/friends/me/invitations", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const receiverId = req.body.account_id;
    logger.info(`${logPrefix} Friend request from ${account.id} to ${receiverId}`);
    const result = await generateInvitation(account.id, receiverId);

    // Send WS notification to receiver (native WBPNFriendRequestReceivedNotification)
    const wsNotif = {
      receiverAccountId: receiverId,
      senderAccountId: account.id,
      invitationId: result.id?.toString() || "",
    };
    await redisClient.publish(FRIEND_REQUEST_WS_CHANNEL, JSON.stringify(wsNotif));

    // Also push DLL notification
    const dllNotif: DLLNotification = {
      type: "friend_request",
      title: "Friend Request",
      message: `${account.username || "Someone"} sent you a friend request!`,
      data: { senderId: account.id, senderUsername: account.username || "Unknown" },
      timestamp: Date.now(),
    };
    await redisPushDLLNotification(receiverId, dllNotif);

    res.send(result);
  } catch (e) {
    logger.error(`${logPrefix} send invitation error: ${e}`);
    res.status(400).send({ error: "Failed to send friend request" });
  }
});

// ─── PUT /accounts/wb_network/bulk ───────────────────────────────────────────
// Game calls this with friend IDs to get detailed account info.
// CRITICAL: All IDs are unified — _id = public_id = wb_network_id.
friendsRouter.put("/accounts/wb_network/bulk", async (req: Request, res: Response) => {
  try {
    const ids = req.body.ids || req.body;
    if (!Array.isArray(ids)) {
      logger.warn(`${logPrefix} bulk: received non-array ids`);
      res.send([]);
      return;
    }
    const contentType = req.headers["content-type"] || "none";
    logger.info(`${logPrefix} PUT /accounts/wb_network/bulk — looking up ${ids.length} IDs: ${JSON.stringify(ids)} (content-type: ${contentType})`);

    const details = await getUserFriendDetails(ids);
    logger.info(`${logPrefix} bulk returned ${details.length} results`);
    res.send(details);
  } catch (e) {
    logger.error(`${logPrefix} bulk error: ${e}`);
    res.send([]);
  }
});

// ─── GET /accounts/wb_network/:id ────────────────────────────────────────────
// Individual account lookup (game calls after receiving friend request notification)
friendsRouter.get("/accounts/wb_network/:id", async (req: Request, res: Response) => {
  try {
    const id = req.params.id;
    // Skip "bulk" path param — that's the PUT endpoint above
    if (id === "bulk") return;
    logger.info(`${logPrefix} GET /accounts/wb_network/${id}`);
    const details = await getUserFriendDetails([id]);
    res.send(details[0] ?? {});
  } catch (e) {
    logger.error(`${logPrefix} wb_network/:id error: ${e}`);
    res.send({});
  }
});

// ─── GET /profiles/search_queries/get-by-username/run ─────────────────────────
// Native search — returns real results instead of DLL notification hack
friendsRouter.get("/profiles/search_queries/get-by-username/run", async (req: Request, res: Response) => {
  try {
    const username = req.query.username as string;
    if (!username) {
      res.send({ cursor: null, start: 0, count: 0, total: 0, results: [] });
      return;
    }
    logger.info(`${logPrefix} Search for username: "${username}"`);
    const results = await searchProfiles(username);
    res.send(results);
  } catch (e) {
    logger.error(`${logPrefix} search error: ${e}`);
    res.send({ cursor: null, start: 0, count: 0, total: 0, results: [] });
  }
});

// ─── PUT /profiles/bulk ──────────────────────────────────────────────────────
friendsRouter.put("/profiles/bulk", async (req: Request, res: Response) => {
  try {
    const ids = req.body.ids || [];
    const pContentType = req.headers["content-type"] || "none";
    logger.info(`${logPrefix} PUT /profiles/bulk — looking up ${ids.length} IDs: ${JSON.stringify(ids)} (content-type: ${pContentType})`);
    const profiles = await getProfileBulk(ids);
    logger.info(`${logPrefix} profiles/bulk returned ${profiles.length} results`);
    res.send(profiles);
  } catch (e) {
    logger.error(`${logPrefix} profiles/bulk error: ${e}`);
    res.send([]);
  }
});

// ─── GET /social/me/blocked ──────────────────────────────────────────────────
friendsRouter.get("/social/me/blocked", async (req: Request, res: Response) => {
  //@ts-ignore TODO : implementation. Remove comment once implemented`

  try {
    const account = AuthUtils.DecodeClientToken(req);
    const mongoPlayer = await PlayerTesterModel.findOne({ id: account.id });
    const fContentType = req.headers["content-type"] || "none";
    const blocked = await getFriends(account.id, "blocked");

    if (mongoPlayer) {
      await ensureNoAssholes(mongoPlayer, account.id);
    }

    logger.info(`${logPrefix} GET /social/me/blocked — returning ${blocked.length} blocked players for ${account.id} (content-type: ${fContentType})`);
    res.send({
      total: blocked.length,
      page: 1,
      page_size: 20,
      results: blocked,
    });
  } catch (e) {
    logger.error(`${logPrefix} GET /social/me/blocked error: ${e}`);
    res.send({ total: 0, page: 1, page_size: 1000, results: [] });
  }
});

friendsRouter.put("/social/me/block/:blockid", async (req: Request<{ blockid: string }, {}, {}, {}>, res: Response) => {
  //@ts-ignore TODO : implementation. Remove comment once implemented`

  if (null == req.params.blockid || undefined == req.params.blockid || req.params.blockid === "") {
    res.status(200).send({});
    return;
  }

  try {
    const account = AuthUtils.DecodeClientToken(req);
    const aID = account.id;
    const blockedPlayerUsername = (await PlayerTesterModel.findOne({ id: req.params.blockid }))?.name || "Unknown";
    const blockResult = await blockPlayer(aID, req.params.blockid, blockedPlayerUsername);
    if (!blockResult.success) {
      logger.error(`${logPrefix} Failed to block player ${req.params.blockid} for account ${aID}: ${blockResult.error}`);
    }
  } catch (e) {
    logger.error(`${logPrefix} PUT /social/me/block/${req.params.blockid}: ${e}`);
  }

  res.status(200).send({});
});

friendsRouter.put("/social/me/unblock/:blockid", async (req: Request<{ blockid: string }, {}, {}, {}>, res: Response) => {
  //@ts-ignore TODO : implementation. Remove comment once implemented`

  if (null == req.params.blockid || undefined == req.params.blockid || req.params.blockid === "") {
    res.status(200).send({});
    return;
  }

  try {
    const account = AuthUtils.DecodeClientToken(req);
    const aID = account.id;
    const unblockResult = await removeFriend(aID, req.params.blockid);
    if (!unblockResult.success) {
      logger.error(`${logPrefix} Failed to unblock player ${req.params.blockid} for account ${aID}: ${unblockResult.error}`);
    }
  } catch (e) {
    logger.error(`${logPrefix} PUT /social/me/unblock/${req.params.blockid}: ${e}`);
  }

  res.status(200).send({});
});

friendsRouter.put("/accounts/me/relationships/:blockid/block", async (req: Request<{ blockid: string }, {}, {}, {}>, res: Response) => {
  //@ts-ignore TODO : implementation. Remove comment once implemented`

  if (null == req.params.blockid || undefined == req.params.blockid || req.params.blockid === "") {
    res.status(200).send({});
    return;
  }

  try {
    const account = AuthUtils.DecodeClientToken(req);
    const aID = account.id;
    const blockedPlayerUsername = (await PlayerTesterModel.findOne({ id: req.params.blockid }))?.name || "Unknown";
    const blockResult = await blockPlayer(aID, req.params.blockid, blockedPlayerUsername);
    if (!blockResult.success) {
      logger.error(`${logPrefix} Failed to block player ${req.params.blockid} for account ${aID}: ${blockResult.error}`);
    }
  } catch (e) {
    logger.error(`${logPrefix} PUT /social/me/block/${req.params.blockid}: ${e}`);
  }

  res.status(200).send({});
});

friendsRouter.put("/accounts/me/relationships/:blockid/unblock", async (req: Request<{ blockid: string }, {}, {}, {}>, res: Response) => {
  //@ts-ignore TODO : implementation. Remove comment once implemented`

  if (null == req.params.blockid || undefined == req.params.blockid || req.params.blockid === "") {
    res.status(200).send({});
    return;
  }

  try {
    const account = AuthUtils.DecodeClientToken(req);
    const aID = account.id;
    const unblockResult = await removeFriend(aID, req.params.blockid);
    if (!unblockResult.success) {
      logger.error(`${logPrefix} Failed to unblock player ${req.params.blockid} for account ${aID}: ${unblockResult.error}`);
    }
  } catch (e) {
    logger.error(`${logPrefix} PUT /social/me/unblock/${req.params.blockid}: ${e}`);
  }

  res.status(200).send({});
});
