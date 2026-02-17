import ObjectID from "bson-objectid";
import redis, { createClient } from "redis";
import type { RedisClientType } from "redis";
import { logger } from "./logger";
import env from "../env/env";
import { cancelMatchmaking, MATCH_TYPES } from "../services/matchmakingService";
import { Cosmetics } from "../database/Cosmetics";
import { GAME_SERVER_PORT } from "../game/udp";
import { AccountToken, IAccountToken } from "../types/AccountToken";
import { num } from "envalid";

const serviceName: string = "Config.Redis";

const redisConfig = {
  username: env.REDIS_USERNAME,
  password: env.REDIS_PW,
  socket: {
    host: env.REDIS,
    port: env.REDIS_PORT,
  },
};

export const ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL = "match:notifications";
export const ALL_PERKS_LOCKED_CHANNEL = "perks:notifications";
export const GAME_SERVER_INSTANCE_READY_CHANNEL = "game_server_ready:notifications";
export const MATCHMAKING_COMPLETE_CHANNEL = "matchmaking:complete";
export const ON_LOBBY_MODE_UPDATED = "OnLobbyModeUpdated";
export const ON_MATCH_MAKER_STARTED_CHANNEL = "party:queued";
export const ON_CANCEL_MATCHMAKING = "matchmaking:cancel";
export const ON_END_OF_MATCH = "match:end";

export type RedisClient = RedisClientType<redis.RedisModules, redis.RedisFunctions, redis.RedisScripts>;

// Create Redis client
export const redisClient = createClient(redisConfig);
export async function startRedis() {
  // Set up event handlers
  redisClient.on("connect", () => {
    logger.info(`[${serviceName}]: Connected to Redis`);
  });

  redisClient.on("error", (err) => {
    logger.error(`[${serviceName}]: Redis error: ${err}`);
  });

  await redisClient.connect();
}

let redisSub: RedisClient | null = null;

// Create a separate client for subscribing
export function initRedisSubscriber() {
  if (!redisSub) {
    redisSub = redisClient.duplicate();
    redisSub.connect();
    redisSub.on("connect", (err) => logger.info(`[${serviceName}]: Connected to SUB Redis`));
    redisSub.on("error", (err) => logger.error(`[${serviceName}]: SUB Redis error: ${err}`));
  }
  return redisSub;
}

export interface RedisLockPerk {
  containerMatchId: string;
  playerId: string;
  perks: string[];
}

export interface RedisMatchPlayer {
  id: string;
  skill: number;
  region: string;
  //partyId: string;
}

export interface RedisMatchTicket {
  party_size: number;
  players: RedisMatchPlayer[];
  created_at: number;
  partyId: string;
  matchmakingRequestId: string;
}

export interface RedisMatch {
  matchId: string;
  resultId: string;
  tickets: RedisMatchTicket[];
  status: string;
  createdAt: number;
  matchType: string;
  totalPlayers: number;
  rollbackPort: number;
}

export interface RedisTeamEntry {
  playerId: string;
  partyId: string;
  playerIndex: number;
  teamIndex: 0 | 1;
  isHost: boolean;
  ip: string;
}

export interface MVS_NOTIFICATION { }

export interface ON_MATCH_MAKER_STARTED_NOTIFICATION extends MVS_NOTIFICATION {
  party_size: number;
  players: RedisMatchPlayer[];
  created_at: number;
  partyId: string;
  matchmakingRequestId: string;
  matchType: MATCH_TYPES;
  partyLeaderId: string;
}

export interface MATCH_FOUND_NOTIFICATION extends MVS_NOTIFICATION {
  players: RedisTeamEntry[];
  matchId: string;
  matchKey: string;
  map: string;
  mode: string;
  rollbackPort: number;
}

export interface RedisMatchEndNotification extends MVS_NOTIFICATION {
  playersIds: string[];
  matchId: string;
}

export interface RedisPlayerConnection extends AccountToken {
  jwt?: string | null | undefined;
  profileIcon?: string;
  character?: string;
  skin?: string;
}

type RedisStatTrackerEntry = [statKey: string, statValue: number];

export interface RedisPlayerConfig {
  taunts: string[];
  RingoutVfx: string;
  Character: string;
  Banner: string;
  StatTrackers: RedisStatTrackerEntry[];
  Perks: string[];
}

export interface RedisAllPerksLockedNotification {
  containerMatchId: string;
  playerIds: string[];
}

export interface RedisGameServerInstanceReadyNotification {
  containerMatchId: string;
  resultId: string;
  playerIds: string[];
  rollbackPort: number;
}

export interface RedisMatchMakingCompleteNotification {
  containerMatchId: string;
  matchmakingRequestId: string;
  resultId: string;
  playerIds: string[];
}

export interface RedisPlayer {
  status: string;
  profileIcon: string;
  character: string;
  skin: string;
  ip: string;
}

export interface RedisOnGameModeUpdatedNotification {
  lobbyId: string;
  playersIds: string[];
  modeString: string;
}

export interface RedisCancelMatchMakingNotification {
  playersIds: string[];
  matchmakingId: string;
}

const MATCH_KEY = (containerMatchId: string) => `match:${containerMatchId}`;
const MATCH_PERKS_KEY = (containerMatchId: string) => `${MATCH_KEY(containerMatchId)}:perks`;
const MATCH_PERKS_PLAYER_KEY = (containerMatchId: string, playerId: string) => `${MATCH_KEY(containerMatchId)}:perks:${playerId}`;

export async function redisCreatePartyLobby() { }

export async function redisGetGamePort(matchId: string) {
  const matchStr = await redisClient.get(MATCH_KEY(matchId));
  if (matchStr) {
    const redisMatch = JSON.parse(matchStr) as RedisMatch;
    return redisMatch.rollbackPort;
  }
}

export async function redisSaveEquippedCosmetics(playerId: string, data: Cosmetics) {
  const result = await redisClient.set(`player:${playerId}:cosmetics`, JSON.stringify(data));
  return result;
}

export async function redisGetAllPlayersEquippedCosmetics(playerIds: string[]) {
  const multi = redisClient.multi();
  for (const playerId of playerIds) {
    multi.get(`player:${playerId}:cosmetics`);
  }
  const cosmeticsStrArray = await multi.exec();
  const Cosmetics = cosmeticsStrArray.map((str) => JSON.parse(str as string) as Cosmetics);

  return Cosmetics;
}

export async function redisGetPlayers(playerIds: string[]) {
  const multi = redisClient.multi();
  for (const playerId of playerIds) {
    multi.hGetAll(`player:${playerId}`);
  }
  const players = (await multi.exec()) as unknown as RedisPlayer[];

  return players;
}

export async function redisGetEquippedCosmetics(playerId: string) {
  const cosmeticsStr = await redisClient.get(`player:${playerId}:cosmetics`);
  if (cosmeticsStr) {
    return JSON.parse(cosmeticsStr) as Cosmetics;
  }
  return null;
}

export async function redisGetMatchTickets(queueKey: string) {
  const ticketsStr = await redisClient.lRange(queueKey, 0, -1);
  const tickets = ticketsStr.map((t) => JSON.parse(t) as RedisMatchTicket);
  return tickets;
}

export async function redisPopMatchTicketsFromQueue(queueType: string, tickets: RedisMatchTicket[]) {
  const multi = redisClient.multi();
  for (const ticket of tickets) {
    multi.lRem(queueType, 0, JSON.stringify(ticket));
    logger.info(`[${serviceName}]: Removed ticket ${ticket.partyId} from ${queueType} queue for match`);
  }
  await multi.exec();
}

export async function redisUpdatePlayerLoadout(playerId: string, redisPlayer: RedisPlayer) {
  // Convert all values to strings for Redis
  const redisPlayerStringObj: Record<string, string> = {};
  for (const [
    key,
    value,
  ] of Object.entries(redisPlayer)) {
    redisPlayerStringObj[key] = value;
  }
  await redisClient.hSet(`player:${playerId}`, redisPlayerStringObj);
}

export async function redisAddPlayerConnection(playerId: string, ip: string, jwt: string, accountToken: IAccountToken) {
  // Convert all values to strings for Redis

  var newConnection: RedisPlayerConnection = accountToken as RedisPlayerConnection;
  newConnection.jwt = jwt;

  const accountTokenStringObj: Record<string, string> = {};
  for (const [
    key,
    value,
  ] of Object.entries(newConnection)) {
    accountTokenStringObj[key] = value;
  }

  await redisClient.hSet(`connections:${playerId}`, accountTokenStringObj);
  await redisClient.hSet(`connections:${ip}`, accountTokenStringObj);
}

export async function redisSetPlayerConnectionCosmetics(playerId: string, cosmetics: Cosmetics) {
  let rPlayerConnectionByID = await redisClient.hGetAll(`connections:${playerId}`) as unknown as RedisPlayerConnection;
  let ip = rPlayerConnectionByID.current_ip;
  let rPlayerConnectionByIP = await redisClient.hGetAll(`connections:${ip}`) as unknown as RedisPlayerConnection;

  const cosmeticStringObj: Record<string, string> = {};
  for (const [
    key,
    value,
  ] of Object.entries(cosmetics)) {
    cosmeticStringObj[key] = JSON.stringify(value);
  }

  await redisClient.hSet(`connections:${playerId}:cosmetics`, cosmeticStringObj);
  await redisClient.hSet(`connections:${ip}:cosmetics`, cosmeticStringObj);

}

export async function redisSetPlayerConnectionByID(playerId: string, connection: RedisPlayerConnection) {
  const connectionStringObj: Record<string, string> = {};
  for (const [
    key,
    value,
  ] of Object.entries(connection)) {
    connectionStringObj[key] = value;
  }
  await redisClient.hSet(`connections:${playerId}`, connectionStringObj);
}

export async function redisSetPlayerConnectionByIp(ip: string, connection: RedisPlayerConnection) {
  const connectionStringObj: Record<string, string> = {};
  for (const [
    key,
    value,
  ] of Object.entries(connection)) {
    connectionStringObj[key] = value;
  }
  await redisClient.hSet(`connections:${ip}`, connectionStringObj);
}

export async function redisGetPlayerConnectionByPlayerId(playerId: string) {
  const connection = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;
  return connection;
}

export async function redisGetPlayerConnectionByIp(ip: string) {
  const connection = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
  return connection;
}

export async function redisUpdatePlayerStatus(playerId: string, status: string) {
  await redisClient.hSet(`player:${playerId}`, { status: status });
}

export async function redisUpdatePlayerKey(playerId: string, key: string, value: string) {
  const prop: Record<string, string> = {};
  prop[key] = value;
  await redisClient.hSet(`player:${playerId}`, prop);
}

export async function redisPushTicketToQueue(queueKey: string, data: RedisMatchTicket) {
  await redisClient.lPush(queueKey, JSON.stringify(data));
}

export async function redisOnMatchMakerStarted(notification: ON_MATCH_MAKER_STARTED_NOTIFICATION) {
  await redisClient.publish(ON_MATCH_MAKER_STARTED_CHANNEL, JSON.stringify(notification));
}

export async function redisOnGameplayConfigNotified(notification: MATCH_FOUND_NOTIFICATION) {
  const EX = 60 * 20;
  await redisClient.set(notification.matchId, JSON.stringify(notification), { EX });
  await redisClient.publish(ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL, JSON.stringify(notification));
}

export async function redisGetMatchConfig(matchId: string) {
  const res = await redisClient.get(matchId);
  return JSON.parse(res as string) as MATCH_FOUND_NOTIFICATION;
}

export async function redisPublisdEndOfMatch(playerIds: string[], matchId: string) {
  const notification: RedisMatchEndNotification = {
    playersIds: playerIds,
    matchId,
  };
  logger.trace(`[${serviceName}]: Publishing ON_END_OF_MATCH`);
  await redisClient.publish(ON_END_OF_MATCH, JSON.stringify(notification));
}

export async function redisGetPlayer(playerId: string) {
  const player = (await redisClient.hGetAll(`player:${playerId}`)) as unknown as RedisPlayer;
  return player;
}

export async function redisGetMatch(containerMatchId: string) {
  const matchStr = await redisClient.get(MATCH_KEY(containerMatchId));
  if (matchStr) {
    const redisMatch = JSON.parse(matchStr) as RedisMatch;
    return redisMatch;
  }
  return null;
}

export async function redisUpdateMatch(containerMatchId: string, match: RedisMatch) {
  const EX = 60 * 20;
  await redisClient.set(MATCH_KEY(containerMatchId), JSON.stringify(match), { EX });
}

export async function redisLockPerks(data: RedisLockPerk) {
  const EX = 60 * 20;
  await redisClient.set(MATCH_PERKS_PLAYER_KEY(data.containerMatchId, data.playerId), JSON.stringify(data.perks), { EX });
}

export async function redisMatchMakingComplete(containerMatchId: string, matchmakingRequestId: string, playerIds: string[]) {
  const notification: RedisMatchMakingCompleteNotification = {
    containerMatchId,
    playerIds,
    matchmakingRequestId,
    resultId: ObjectID().toHexString(),
  };
  await redisClient.publish(MATCHMAKING_COMPLETE_CHANNEL, JSON.stringify(notification));
}

export async function redisGameServerInstanceReady(containerMatchId: string, playerIds: string[]) {
  const notification: RedisGameServerInstanceReadyNotification = {
    containerMatchId,
    playerIds,
    resultId: ObjectID().toHexString(),
    rollbackPort: await redisGetGamePort(containerMatchId) || GAME_SERVER_PORT,
  };
  await redisClient.publish(GAME_SERVER_INSTANCE_READY_CHANNEL, JSON.stringify(notification));
}

export async function redisPublishAllPerksLocked(containerMatchId: string, playerIds: string[]) {
  const notification: RedisAllPerksLockedNotification = { containerMatchId, playerIds };
  await redisClient.publish(ALL_PERKS_LOCKED_CHANNEL, JSON.stringify(notification));
  logger.info(`[${serviceName}]: All perks locked ${containerMatchId}, players, (${playerIds.join(",")})`);
}

export async function redisGetPlayerPerk(containerMatchId: string, playerId: string) {
  const playerPerkStr = await redisClient.GET(MATCH_PERKS_PLAYER_KEY(containerMatchId, playerId));
  if (playerPerkStr) {
    const playerPerk = JSON.parse(playerPerkStr) as string[];
    return playerPerk;
  }
  return null;
}

export async function redisGetAllLockedPerks(containerMatchId: string) {
  const matchStr = await redisClient.get(MATCH_KEY(containerMatchId));
  if (matchStr) {
    const redisMatch = JSON.parse(matchStr) as RedisMatch;
    const multi = redisClient.multi();

    const playersIds = redisMatch.tickets.flatMap((ticket) => ticket.players.map((player) => player.id));
    for (const playerId of playersIds) {
      multi.get(MATCH_PERKS_PLAYER_KEY(containerMatchId, playerId));
    }
    const results = await multi.exec();
    if (results) {
      let playerIndex = 0;
      const perks = results.map((reply) => {
        return { playerId: playersIds[playerIndex++], perks: JSON.parse(reply as string) as string[] };
      });
      return perks;
    }
  }

  logger.error(`[${serviceName}]: Failed to get all locked perks for match ${containerMatchId}`);
  const redisMatch = JSON.parse(matchStr || "") as RedisMatch;

  for (let numberOfTickets = 0; numberOfTickets < redisMatch.tickets.length; numberOfTickets++) {
    var ticket = redisMatch.tickets[numberOfTickets];
    var matchmakingRequest = ticket.matchmakingRequestId;

    for (const player of ticket.players) {
      var rPlayerConnectionByID = await redisClient.hGetAll(`connections:${player.id}`) as unknown as RedisPlayerConnection;
      logger.error(`[${serviceName}]: Canceling matchmaking for player ${player.id} with name ${rPlayerConnectionByID.username} and IP ${rPlayerConnectionByID.current_ip}, matchmaking request ${matchmakingRequest} due to an error retrieving locked perks`);
      cancelMatchmaking(player.id, matchmakingRequest);
    }
  }
}

export async function redisDeleteKeysByPattern(prefix: string, pattern: string): Promise<void> {
  const query = `${prefix}:${pattern}*`;
  let cursor = 0;

  do {
    const result = await redisClient.scan(cursor, {
      MATCH: query,
      COUNT: 100,
    });

    cursor = Number(result.cursor);
    const keys = result.keys;

    if (keys.length > 0) {
      await redisClient.del(keys);
      logger.info(`[${serviceName}]: Deleted keys: ${keys.join(", ")}`);
    }
  } while (cursor !== 0);

  logger.info(`[${serviceName}]: All keys matching "${query}" have been deleted.`);
}

export async function redisDeletePlayerKeys(playerId: string): Promise<void> {
  await redisDeleteKeysByPattern("player", `${playerId}`);
  await redisDeleteKeysByPattern(`connections`, `${playerId}`);
}

export async function redisDeleteConnectionKeysByIp(ip: string): Promise<void> {
  await redisDeleteKeysByPattern(`connections`, `${ip}`);
}

export async function redisDeleteConnectionKeysById(playerId: string): Promise<void> {
  await redisDeleteKeysByPattern(`connections`, `${playerId}`);
}

