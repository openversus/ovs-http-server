import express, { Request, Response } from "express";
import { MVSQueries } from "../interfaces/queries_types";
import { redisClient, redisGetOnlinePlayers, RedisPlayerConnection } from "../config/redis";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { ensureNoAssholes } from "../services/friendService";
import { logger, logwrapper } from "../config/logger";
import * as AuthUtils from "../utils/auth";
import * as KitchenSink from "../utils/garbagecan";

const serviceName = "Handlers.Accounts";
const logPrefix = `[${serviceName}]:`;


// Import the dynamic profile lookup used by the friendsRouter so this handler
// matches whatever the friends side returns. Previously this returned a static
// array of 67 hardcoded test accounts (KappaPingWarrior, etc.) which surfaced
// as random names in players blocked-list and friends-list UIs whenever the
// game called this bulk-resolver to populate display data for the IDs in their
// FriendList. The friendsRouter version of /accounts/wb_network/bulk already
// runs before this in middleware order so it usually wins, but registering a
// stub-static fallback here means any path that bypasses friendsRouter (or any
// future direct caller of this handler) will still produce correct data.
import { getUserFriendDetails } from "../modules/friends/friends.service";

export async function handleAccounts_wb_network_bulk(req: Request<{}, {}, {}, MVSQueries.Accounts_wb_network_bulk_QUERY>, res: Response) {
  try {
    const ids = (req.body as any)?.ids ?? (Array.isArray(req.body) ? req.body : []);
    if (!Array.isArray(ids)) {
      logger.warn(`${logPrefix} /accounts/wb_network/bulk: received non-array ids`);
      res.send([]);
      return;
    }
    logger.info(`${logPrefix} /accounts/wb_network/bulk — looking up ${ids.length} IDs: ${JSON.stringify(ids)}`);
    const details = await getUserFriendDetails(ids);
    logger.info(`${logPrefix} /accounts/wb_network/bulk returned ${details.length} results`);
    res.send(details);
  } catch (e) {
    logger.error(`${logPrefix} /accounts/wb_network/bulk error: ${e}`);
    res.send([]);
  }
}


export async function handleAccounts_me_relationships_block(req: Request<{ blockid: string }, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;
  const playerUsername = account.username;
  const blockedPlayer = req.params.blockid as string ?? "";

  if (!aID || blockedPlayer === "") {
    res.status(200).send({});
    return;
  }

  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${aID}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.warn(`${logPrefix} No Redis player connection found for player ID ${aID}, cannot set loadout.`);
  }

  let mongoPlayer = await PlayerTesterModel.findOne({ id: aID });
  if (!mongoPlayer) {
    logger.warn(`${logPrefix} No Mongo player found for player ID ${aID}, cannot add blocked player .`);
    res.status(200).send({});
    return;
  }

  let mongoBlockedPlayer = await PlayerTesterModel.findOne({ id: blockedPlayer });
  if (!mongoBlockedPlayer) {
    logger.warn(`${logPrefix} No Mongo player found for blocked player ID ${blockedPlayer}, cannot add to blocked list.`);
    res.status(200).send({});
    return;
  }

  let blockedPlayers = mongoPlayer.blockedPlayers || [];
  if (!blockedPlayers.includes(blockedPlayer)) {
    blockedPlayers.push(blockedPlayer);
    mongoPlayer.blockedPlayers = blockedPlayers;
    await mongoPlayer.save();
    await ensureNoAssholes(mongoPlayer, mongoPlayer.id);
    logger.info(`${logPrefix} Player ${playerUsername} (${aID}) blocked player ${mongoBlockedPlayer.name} (${blockedPlayer}).`);
  } else {
    logger.info(`${logPrefix} Player ${playerUsername} (${aID}) attempted to block player ${mongoBlockedPlayer.name} (${blockedPlayer}), but they were already blocked.`);
  }

  res.status(200).send({});
}

export async function handleAccounts_me_relationships_unblock(req: Request<{ blockid: string }, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;
  const playerUsername = account.username;
  const unblockedPlayer = req.params.blockid as string ?? "";

  if (!aID || unblockedPlayer === "") {
    res.status(200).send({});
    return;
  }

  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${aID}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.warn(`${logPrefix} No Redis player connection found for player ID ${aID}, cannot set loadout.`);
  }

  let mongoPlayer = await PlayerTesterModel.findOne({ id: aID });
  if (!mongoPlayer) {
    logger.warn(`${logPrefix} No Mongo player found for player ID ${aID}, cannot add blocked player .`);
    res.status(200).send({});
    return;
  }

  let mongoUnblockedPlayer = await PlayerTesterModel.findOne({ id: unblockedPlayer });
  if (!mongoUnblockedPlayer) {
    logger.warn(`${logPrefix} No Mongo player found for unblocked player ID ${unblockedPlayer}, cannot remove from blocked list.`);
    res.status(200).send({});
    return;
  }

  let blockedPlayers = mongoPlayer.blockedPlayers || [];
  if (blockedPlayers.includes(unblockedPlayer)) {
    blockedPlayers = blockedPlayers.filter(id => id !== unblockedPlayer);
    mongoPlayer.blockedPlayers = blockedPlayers;
    await mongoPlayer.save();
    logger.info(`${logPrefix} Player ${playerUsername} (${aID}) unblocked player ${mongoUnblockedPlayer.name} (${unblockedPlayer}).`);
  } else {
    logger.info(`${logPrefix} Player ${playerUsername} (${aID}) attempted to unblock player ${mongoUnblockedPlayer.name} (${unblockedPlayer}), but they were not blocked.`);
  }

  res.status(200).send({});
}
