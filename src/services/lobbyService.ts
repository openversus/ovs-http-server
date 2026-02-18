import ObjectID from "bson-objectid";
import {
  ON_LOBBY_MODE_UPDATED,
  redisClient,
  redisSetPlayerConnectionByID,
  redisSetPlayerConnectionByIp,
  RedisPlayerConnection,
  RedisOnGameModeUpdatedNotification,
} from "../config/redis";
import { logger } from "../config/logger";
import { getEquippedCosmetics } from "./cosmeticsService";
import * as SharedTypes from "../types/shared-types";
import env from "../env/env";
import * as KitchenSink from "../utils/garbagecan";

const serviceName = "Services.Lobby";
const BE_VERBOSE = env.VERBOSE_LOGGING === 1 ? true : false;

export enum LOBBY_MODES {
  ONE_V_ONE = "1v1",
  TWO_V_TWO = "2v2",
  FFA = "FFA",
  ONE_V_ONE_RANKED = "1v1_ranked",
  TWO_V_TWO_RANKED = "2v2_ranked",
  // ONE_V_ONE_CUSTOM = "custom_container_two_player",
  // TWO_V_TWO_CUSTOM = "custom_container_four_player",
  // ONE_V_TWO_CUSTOM = "custom_container_three_player",
}

interface Lobby {
  id: string;
  created_at: string;
  owner: string;
  guest?: string;
  mode: LOBBY_MODES;
}

export async function createLobby(accountId: string, lobbyMode: LOBBY_MODES = LOBBY_MODES.ONE_V_ONE): Promise<Lobby> {
  const lobbyId = ObjectID().toHexString();
  const newLobby: Lobby = {
    id: lobbyId,
    created_at: new Date().toISOString(),
    mode: lobbyMode, // Default mode, can be changed later
    owner: accountId,
    // guest can be set later when a second player joins
  };
  await redisClient.hSet(`player:${accountId}:lobby:${lobbyId}`, {
    id: newLobby.id,
    created_at: newLobby.created_at,
    mode: newLobby.mode,
    owner: newLobby.owner,
  });

  logger.info(`[${serviceName}]: Creating party lobby for ${accountId} - matchLobbyId:${lobbyId}`);

  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${accountId}`)) as unknown as RedisPlayerConnection;
  rPlayerConnectionByID.lobby_id = lobbyId;
  await redisSetPlayerConnectionByID(accountId, rPlayerConnectionByID);

  return newLobby;
}

export async function changeLobbyMode(ownerId: string, lobbyId: string, newMode: LOBBY_MODES) {
  logger.info(`[${serviceName}]: Received changeLobbyMode request (ownerid, lobbyid, newmode): ${ownerId}, ${lobbyId}, ${newMode}`);

  const lobby = await redisClient.hGetAll(`player:${ownerId}:lobby:${lobbyId}`);
  if (!lobby.id) {
    logger.error(`[${serviceName}]: Lobby not found for id: ${lobbyId}`);
    return;
  }
  if (lobby.owner !== ownerId) {
    logger.error(`[${serviceName}]: You are not the owner of this lobby`);
    return;
  }

  await redisClient.hSet(`player:${ownerId}:lobby:${lobbyId}`, { mode: newMode });
  const notification: RedisOnGameModeUpdatedNotification = {
    lobbyId: lobbyId,
    playersIds: [ownerId],
    modeString: newMode,
  };
  await redisClient.publish(ON_LOBBY_MODE_UPDATED, JSON.stringify(notification));
  logger.trace(`[${serviceName}]: Changing party lobby for ${lobbyId} - to ${newMode}`);

  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${ownerId}`)) as unknown as RedisPlayerConnection;
  let rPlayerIP = rPlayerConnectionByID.current_ip;
  let rPlayerConnectionByIP = (await redisClient.hGetAll(`connections:${rPlayerIP}`)) as unknown as RedisPlayerConnection;

  let rPlayerConnectionValues = Object.values(rPlayerConnectionByID);
  let allConnections = await redisClient.keys(`connections:*`);
  let allconnectionsValues = [];
  for (const key of allConnections) {
    const connectionData = await redisClient.hGetAll(key);
    allconnectionsValues.push({ key, data: connectionData });
  }

  if (BE_VERBOSE) {
    logger.info(`Redis connection by player ID ${ownerId} : ${JSON.stringify(rPlayerConnectionByID)}`);
    logger.info(`Player values for player ID ${ownerId} : ${JSON.stringify(rPlayerConnectionValues)}`);
    logger.info(`Player values for player ID ${ownerId} via KitchenSink: `);
    KitchenSink.TryInspect(rPlayerConnectionValues);

    logger.info("Connections via KitchenSink by ID: ");
    KitchenSink.TryInspect(rPlayerConnectionByID);

    logger.info("All Connections via KitchenSink: ");
    KitchenSink.TryInspect(allConnections);
  }

  if (lobbyId && lobbyId !== "undefined" && lobbyId !== rPlayerConnectionByID.lobby_id) {
    logger.info(`Updating lobby_id for player ${ownerId} in Redis to ${lobbyId}`);
    rPlayerConnectionByID.lobby_id = lobbyId;
    rPlayerConnectionByIP.lobby_id = lobbyId;
    await redisSetPlayerConnectionByID(ownerId, rPlayerConnectionByID);
    await redisSetPlayerConnectionByIp(rPlayerIP, rPlayerConnectionByIP);
  }

  return lobby;
}
