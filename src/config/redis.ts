import ObjectID from "bson-objectid";
import redis, { createClient } from "redis";
import type { RedisClientType } from "redis";
import { logger } from "./logger";
import env from "../env/env";
import { cancelMatchmaking, MATCH_TYPES } from "../services/matchmakingService";
import { Cosmetics } from "../database/Cosmetics";
import { GAME_SERVER_PORT } from "../game/udp";
import { AccountToken, IAccountToken } from "../types/AccountToken";
import { IDeployInfo } from "../services/rollbackService";
import { num } from "envalid";

const serviceName: string = "Config.Redis";
const logPrefix = `[${serviceName}]:`;

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
export const LOBBY_REJOIN_CHANNEL = "lobby:rejoin";
export const PLAYER_LOADOUT_LOCKED_CHANNEL = "lobby:loadout_locked";
export const LOBBY_RETURN_CHANNEL = "lobby:return";
export const FRIEND_REQUEST_WS_CHANNEL = "friend:request:ws";

export interface RedisFriendRequestWSNotification {
  receiverAccountId: string;
  senderAccountId: string;
  invitationId: string;
}

export type RedisClient = RedisClientType<redis.RedisModules, redis.RedisFunctions, redis.RedisScripts>;

// Create Redis client
export const redisClient = createClient(redisConfig);
export async function startRedis() {
  // Set up event handlers
  redisClient.on("connect", () => {
    logger.info(`${logPrefix} Connected to Redis`);
  });

  redisClient.on("error", (err) => {
    logger.error(`${logPrefix} Redis error: ${err}`);
  });

  await redisClient.connect();
}

let redisSub: RedisClient | null = null;

// Create a separate client for subscribing
export function initRedisSubscriber() {
  if (!redisSub) {
    redisSub = redisClient.duplicate();
    redisSub.connect();
    redisSub.on("connect", (err) => logger.info(`${logPrefix} Connected to SUB Redis`));
    redisSub.on("error", (err) => logger.error(`${logPrefix} SUB Redis error: ${err}`));
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
  isPasswordMatch?: boolean;
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
  isPasswordMatch?: boolean;
}

export interface RedisTeamEntry {
  playerId: string;
  partyId: string;
  playerIndex: number;
  teamIndex: 0 | 1 | 2 | 3;
  isHost: boolean;
  ip: string;
  isSpectator?: boolean;
}

export interface MVS_NOTIFICATION {}

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
  // Custom game settings (injected into handleSendGamePlayConfig)
  isCustomGame?: boolean;
  customNumRingouts?: number;
  customMatchTime?: number;
  customHazards?: boolean;
  customShields?: boolean;
  worldBuffs?: string[];
  playerBuffs?: Record<string, string[]>;
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
  GameplayPreferences: number;
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

export async function redisCreatePartyLobby() {}

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
    logger.info(`${logPrefix} Removed ticket ${ticket.partyId} from ${queueType} queue for match`);
  }
  await multi.exec();
}

/**
 * Silently removes any existing matchmaking tickets for a player from all queues.
 * Does NOT publish any Redis notifications or trigger cancel flows — just a direct removal.
 * Used at the start of matchmaking to clean up stale tickets before creating new ones.
 */
export async function redisRemoveExistingTicketsForPlayer(playerId: string): Promise<number> {
  const queues = ["1v1", "2v2"];
  let removed = 0;
  for (const queue of queues) {
    const ticketsStr = await redisClient.lRange(queue, 0, -1);
    for (const ticketStr of ticketsStr) {
      const ticket = JSON.parse(ticketStr) as RedisMatchTicket;
      if (ticket.players.some((p) => p.id === playerId)) {
        await redisClient.lRem(queue, 0, ticketStr);
        logger.info(`${logPrefix} Silently removed stale ticket ${ticket.partyId} from ${queue} queue for player ${playerId}`);
        removed++;
      }
    }
  }
  return removed;
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
  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;
  let ip = rPlayerConnectionByID.current_ip;
  let rPlayerConnectionByIP = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;

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

export async function redisGetPlayerConnectionByPlayerIDAsync(playerId: string) {
  const connection = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;
  return connection;
}

export function redisGetPlayerConnectionByPlayerID(playerId: string) {
  return redisClient.hGetAll(`connections:${playerId}`).then((connection) => connection as unknown as RedisPlayerConnection);
}

export async function redisGetPlayerConnectionByIPAsync(ip: string) {
  const connection = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
  return connection;
}

export function redisGetPlayerConnectionByIP(ip: string) {
  return redisClient.hGetAll(`connections:${ip}`).then((connection) => connection as unknown as RedisPlayerConnection);
}

export async function redisSaveIdentity(ip: string, steamId: string, epicId: string, hardwareId: string) {
  await redisClient.hSet(`identity:${ip}`, { steamId, epicId, hardwareId });
  await redisClient.expire(`identity:${ip}`, 300); // 5 min TTL, enough for access handshake
}

export async function redisGetIdentity(ip: string): Promise<{ steamId: string; epicId: string; hardwareId: string } | null> {
  const data = await redisClient.hGetAll(`identity:${ip}`);
  if (!data || Object.keys(data).length === 0) return null;
  return { steamId: data.steamId ?? "", epicId: data.epicId ?? "", hardwareId: data.hardwareId ?? "" };
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
  await redisClient.rPush(queueKey, JSON.stringify(data));
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
  logger.trace(`${logPrefix} Publishing ON_END_OF_MATCH`);
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
    rollbackPort: (await redisGetGamePort(containerMatchId)) || GAME_SERVER_PORT,
  };
  await redisClient.publish(GAME_SERVER_INSTANCE_READY_CHANNEL, JSON.stringify(notification));
}

export async function redisPublishAllPerksLocked(containerMatchId: string, playerIds: string[]) {
  const notification: RedisAllPerksLockedNotification = { containerMatchId, playerIds };
  await redisClient.publish(ALL_PERKS_LOCKED_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} All perks locked ${containerMatchId}, players, (${playerIds.join(",")})`);
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

  logger.error(`${logPrefix} Failed to get all locked perks for match ${containerMatchId}`);
  const redisMatch = JSON.parse(matchStr || "") as RedisMatch;

  for (let numberOfTickets = 0; numberOfTickets < redisMatch.tickets.length; numberOfTickets++) {
    var ticket = redisMatch.tickets[numberOfTickets];
    var matchmakingRequest = ticket.matchmakingRequestId;

    for (const player of ticket.players) {
      var rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${player.id}`)) as unknown as RedisPlayerConnection;
      logger.error(
        `[${serviceName}]: Canceling matchmaking for player ${player.id} with name ${rPlayerConnectionByID.username} and IP ${rPlayerConnectionByID.current_ip}, matchmaking request ${matchmakingRequest} due to an error retrieving locked perks`,
      );
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
      logger.info(`${logPrefix} Deleted keys: ${keys.join(", ")}`);
    }
  } while (cursor !== 0);

  logger.info(`${logPrefix} All keys matching "${query}" have been deleted.`);
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

export async function redisAddDeployedRollbackServer(containerMatchId: string, deployInfo: IDeployInfo): Promise<void> {
  await redisClient.set(`rollback:${containerMatchId}`, JSON.stringify(deployInfo));
}

export async function redisDeleteDeployedRollbackServer(containerMatchId: string): Promise<void> {
  await redisClient.del(`rollback:${containerMatchId}`);
}

export async function redisGetDeployedRollbackServer(containerMatchId: string): Promise<IDeployInfo | null> {
  const deployInfoStr = await redisClient.get(`rollback:${containerMatchId}`);
  if (deployInfoStr) {
    return JSON.parse(deployInfoStr) as IDeployInfo;
  }
  return null;
}

export async function redisGetAllDeployedRollbackServers(): Promise<Record<string, IDeployInfo>> {
  const rollbackServers: Record<string, IDeployInfo> = {};
  let cursor = 0;

  do {
    const result = await redisClient.scan(cursor, {
      MATCH: `rollback:*`,
      COUNT: 100,
    });

    cursor = Number(result.cursor);
    const keys = result.keys;

    for (const key of keys) {
      const containerMatchId = key.split(":")[1];
      const deployInfoStr = await redisClient.get(key);
      if (deployInfoStr) {
        rollbackServers[containerMatchId] = JSON.parse(deployInfoStr) as IDeployInfo;
      }
    }
  } while (cursor !== 0);
  
  return rollbackServers;
}

// --- Online Players Tracking ---

const ONLINE_PLAYERS_SET = "online_players";

export async function redisAddOnlinePlayer(playerId: string): Promise<void> {
  await redisClient.sAdd(ONLINE_PLAYERS_SET, playerId);
  logger.info(`${logPrefix} Added player ${playerId} to online players set`);
}

export async function redisRemoveOnlinePlayer(playerId: string): Promise<void> {
  await redisClient.sRem(ONLINE_PLAYERS_SET, playerId);
  logger.info(`${logPrefix} Removed player ${playerId} from online players set`);
}

export async function redisGetOnlinePlayers(): Promise<string[]> {
  return await redisClient.sMembers(ONLINE_PLAYERS_SET);
}

// --- Party Invites ---

export const PARTY_INVITE_CHANNEL = "party:invite";
export const TOAST_RECEIVED_CHANNEL = "toast:received";

export interface RedisPartyInviteNotification {
  inviterAccountId: string;
  inviterUsername: string;
  invitedAccountId: string;
  matchId: string;
  lobbyId: string;
}

export async function redisPublishPartyInvite(notification: RedisPartyInviteNotification): Promise<void> {
  await redisClient.publish(PARTY_INVITE_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published party invite from ${notification.inviterAccountId} to ${notification.invitedAccountId} for match ${notification.matchId}`);
}

// --- DLL Notification Queue ---
// Pending notifications polled by the DLL via GET /ovs/notifications/:playerId

export interface DLLNotification {
  type: string;           // "party_invite", etc.
  title: string;
  message: string;
  data: Record<string, any>;
  timestamp: number;
}

const DLL_NOTIF_KEY_PREFIX = "dll_notifications:";
const DLL_NOTIF_TTL = 60; // expire after 60 seconds if unpolled

export async function redisPushDLLNotification(playerId: string, notification: DLLNotification): Promise<void> {
  const key = `${DLL_NOTIF_KEY_PREFIX}${playerId}`;
  await redisClient.rPush(key, JSON.stringify(notification));
  await redisClient.expire(key, DLL_NOTIF_TTL);
  logger.info(`${logPrefix} Queued DLL notification for ${playerId}: ${notification.type}`);
}

export async function redisPopDLLNotifications(playerId: string): Promise<DLLNotification[]> {
  const key = `${DLL_NOTIF_KEY_PREFIX}${playerId}`;
  const items: string[] = [];
  // Pop all items from the list
  let item = await redisClient.lPop(key);
  while (item) {
    items.push(item);
    item = await redisClient.lPop(key);
  }
  return items.map((s) => JSON.parse(s) as DLLNotification);
}

// --- Toast Notifications ---

export interface RedisToastNotification {
  toasterAccountId: string;
  toasterUsername: string;
  toasteeAccountId: string;
  containerMatchId: string;
}

export async function redisPublishToast(notification: RedisToastNotification): Promise<void> {
  await redisClient.publish(TOAST_RECEIVED_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published toast from ${notification.toasterAccountId} (${notification.toasterUsername}) to ${notification.toasteeAccountId} for match ${notification.containerMatchId}`);
}

// --- Lobby State ---

export const PLAYER_JOINED_LOBBY_CHANNEL = "lobby:player_joined";

export interface RedisLobbyState {
  lobbyId: string;
  ownerId: string;
  ownerUsername: string;
  mode: string;
  playerIds: string[];
  createdAt: number;
}

export interface RedisPlayerJoinedLobbyNotification {
  lobbyId: string;
  ownerId: string;
  joinedPlayerId: string;
  joinedPlayerUsername: string;
  allPlayerIds: string[];
  mode: string;
}

export async function redisSaveLobbyState(lobbyId: string, state: RedisLobbyState): Promise<void> {
  // Solo lobbies expire in 1 hour, party lobbies in 8 hours.
  // Solo lobbies are cheap to recreate; party lobbies must survive long sessions.
  const EX = state.playerIds.length >= 2 ? 8 * 60 * 60 : 60 * 60;
  await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(state), { EX });
  logger.info(`${logPrefix} Saved lobby state for ${lobbyId} with players: ${state.playerIds.join(", ")}`);
}

export async function redisGetLobbyState(lobbyId: string): Promise<RedisLobbyState | null> {
  const data = await redisClient.get(`lobby:${lobbyId}`);
  if (data) {
    return JSON.parse(data) as RedisLobbyState;
  }
  return null;
}

export async function redisPublishPlayerJoinedLobby(notification: RedisPlayerJoinedLobbyNotification): Promise<void> {
  await redisClient.publish(PLAYER_JOINED_LOBBY_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published player joined lobby: ${notification.joinedPlayerId} joined ${notification.lobbyId}`);
}

// --- Targeted Lobby Join (sent to ONE player to transition them into a lobby) ---
export const LOBBY_TRANSITION_CHANNEL = "lobby:transition";

export interface RedisLobbyTransitionNotification {
  targetPlayerId: string;   // The specific player who should receive lobby-join
  lobbyId: string;          // The real lobby ID (inviter's lobby) — used for data lookups
  wsLobbyId?: string;       // Override lobby ID in WS messages (invitee's old lobby ID)
                            // so the game accepts them (it filters by current lobby ID)
  ownerId: string;
  joinedPlayerId: string;   // The player who is joining (for PlayerJoinedLobby msg)
  joinedPlayerUsername: string;
  allPlayerIds: string[];
  mode: string;
}

export async function redisPublishLobbyTransition(notification: RedisLobbyTransitionNotification): Promise<void> {
  await redisClient.publish(LOBBY_TRANSITION_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published lobby transition for ${notification.targetPlayerId} to lobby ${notification.lobbyId}`);
}

export interface RedisLobbyRejoinNotification {
  playerId: string;  // The player who needs to rejoin (owner/Player 1)
  lobbyId: string;
}

export async function redisPublishLobbyRejoin(notification: RedisLobbyRejoinNotification): Promise<void> {
  await redisClient.publish(LOBBY_REJOIN_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published lobby rejoin request for player ${notification.playerId} lobby ${notification.lobbyId}`);
}

// --- OnPlayerLoadoutLocked notification ---
// Sent to party members when a player locks their loadout or joins a lobby.
// Triggers the game's native HandleOnLoadoutLockedForPlayer handler,
// which shows the golden swirl character spawn animation.
export interface RedisPlayerLoadoutLockedNotification {
  lobbyId: string;
  targetPlayerIds: string[];  // Players who should RECEIVE this notification
  lockedPlayerId: string;     // The player whose loadout is being reported
  character: string;
  skin: string;
  isJoining?: boolean;        // If true, also send PlayerJoinedLobby to update game roster
}

export async function redisPublishPlayerLoadoutLocked(notification: RedisPlayerLoadoutLockedNotification): Promise<void> {
  await redisClient.publish(PLAYER_LOADOUT_LOCKED_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published loadout locked: ${notification.lockedPlayerId} in ${notification.lobbyId} (char: ${notification.character})`);
}

export async function redisPublishOnLobbyModeUpdated(notification: RedisOnGameModeUpdatedNotification): Promise<void> {
  await redisClient.publish(ON_LOBBY_MODE_UPDATED, JSON.stringify(notification));
  logger.info(`${logPrefix} Published lobby mode update: ${notification.lobbyId} mode=${notification.modeString} to players ${notification.playersIds.join(", ")}`);
}

// --- Lobby Return (RematchDeclinedNotification) ---
// Sends RematchDeclinedNotification via WS to force the game to "return to lobby",
// which triggers the native create_party_lobby call. Used for party invite accept
// so the invitee's game re-fetches the shared lobby without a WS disconnect.
export interface RedisLobbyReturnNotification {
  playerId: string;
  matchId: string;  // Can be any ID — game uses it for internal tracking
}

export async function redisPublishLobbyReturn(notification: RedisLobbyReturnNotification): Promise<void> {
  await redisClient.publish(LOBBY_RETURN_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published lobby return (RematchDeclined) for player ${notification.playerId}`);
}

// --- Party Member Join Notification (AccelByte text-format via Hydra WS) ---
export const PARTY_MEMBER_JOIN_CHANNEL = "party:member_joined";

export interface RedisPartyMemberJoinNotification {
  targetPlayerId: string;  // who to send to (the inviter)
  joinedUserId: string;    // who joined (the invitee)
}

export async function redisPublishPartyMemberJoin(notification: RedisPartyMemberJoinNotification): Promise<void> {
  await redisClient.publish(PARTY_MEMBER_JOIN_CHANNEL, JSON.stringify(notification));
  logger.info(`${logPrefix} Published partyMemberJoinNotif: ${notification.joinedUserId} joined → notify ${notification.targetPlayerId}`);
}

// --- Lobby Redirects ---
// When player B is force-joined into player A's lobby, we store a redirect
// so that when player B's client fetches their lobby, they get player A's lobby instead
export async function redisSaveLobbyRedirect(fromLobbyId: string, toLobbyId: string): Promise<void> {
  const EX = 60 * 60; // 1 hour expiry
  await redisClient.set(`lobby_redirect:${fromLobbyId}`, toLobbyId, { EX });
  logger.info(`${logPrefix} Saved lobby redirect: ${fromLobbyId} -> ${toLobbyId}`);
}

export async function redisGetLobbyRedirect(lobbyId: string): Promise<string | null> {
  return await redisClient.get(`lobby_redirect:${lobbyId}`);
}

// Store which lobby a player is currently in (so we can redirect their match fetches)
export async function redisSavePlayerLobby(playerId: string, lobbyId: string): Promise<void> {
  const EX = 8 * 60 * 60; // 8 hours — long enough for any gaming session
  await redisClient.set(`player_lobby:${playerId}`, lobbyId, { EX });
  logger.info(`${logPrefix} Player ${playerId} is now in lobby ${lobbyId}`);
}

export async function redisGetPlayerLobby(playerId: string): Promise<string | null> {
  return await redisClient.get(`player_lobby:${playerId}`);
}

export async function redisDeletePlayerLobby(playerId: string): Promise<void> {
  await redisClient.del(`player_lobby:${playerId}`);
  logger.info(`${logPrefix} Deleted player_lobby for ${playerId}`);
}

export async function redisDeleteLobbyState(lobbyId: string): Promise<void> {
  await redisClient.del(`lobby:${lobbyId}`);
  logger.info(`${logPrefix} Deleted lobby state for ${lobbyId}`);
}

// pending_join_lobby — set by accept-invite / party_joined, consumed by join_party_lobby.
// This keeps player_lobby pointing at the OLD (solo) lobby so leave_player_lobby
// destroys the right thing while the player transitions between lobbies.
export async function redisSetPendingJoinLobby(playerId: string, lobbyId: string): Promise<void> {
  await redisClient.set(`pending_join_lobby:${playerId}`, lobbyId, { EX: 60 });
  logger.info(`${logPrefix} Set pending_join_lobby for ${playerId} → ${lobbyId}`);
}

export async function redisGetPendingJoinLobby(playerId: string): Promise<string | null> {
  return await redisClient.get(`pending_join_lobby:${playerId}`);
}

export async function redisDeletePendingJoinLobby(playerId: string): Promise<void> {
  await redisClient.del(`pending_join_lobby:${playerId}`);
}

// Clean up a player's lobby data on disconnect.
// Removes them from the lobby's player list, deletes their player_lobby key,
// and if the lobby is now empty, deletes the lobby state entirely.
export async function redisCleanupPlayerLobby(playerId: string): Promise<void> {
  const lobbyId = await redisGetPlayerLobby(playerId);
  if (!lobbyId) return;

  // Remove player_lobby mapping
  await redisDeletePlayerLobby(playerId);

  // Remove player from the lobby state
  const lobbyState = await redisGetLobbyState(lobbyId);
  if (lobbyState) {
    lobbyState.playerIds = lobbyState.playerIds.filter(pid => pid !== playerId);
    if (lobbyState.playerIds.length === 0) {
      // Lobby is empty, delete it
      await redisDeleteLobbyState(lobbyId);
    } else {
      // Save updated lobby without this player
      await redisSaveLobbyState(lobbyId, lobbyState);
    }
  }

  // Also clean up any lobby redirects
  const redirect = await redisGetLobbyRedirect(lobbyId);
  if (redirect) {
    await redisClient.del(`lobby_redirect:${lobbyId}`);
  }

  logger.info(`${logPrefix} Cleaned up lobby data for disconnected player ${playerId}`);
}

// --- Party Key (web-based party join) ---

export interface RedisPartyKeyData {
  playerId: string;
  lobbyId: string;
  username: string;
}

export async function redisSavePartyKey(key: string, data: RedisPartyKeyData): Promise<void> {
  const EX = 60 * 60; // 1 hour TTL
  await redisClient.set(`party_key:${key.toLowerCase()}`, JSON.stringify(data), { EX });
  logger.info(`${logPrefix} Saved party key "${key}" for player ${data.playerId} (${data.username})`);
}

export async function redisGetPartyKey(key: string): Promise<RedisPartyKeyData | null> {
  const data = await redisClient.get(`party_key:${key.toLowerCase()}`);
  if (data) {
    return JSON.parse(data) as RedisPartyKeyData;
  }
  return null;
}

export async function redisDeletePartyKey(key: string): Promise<void> {
  await redisClient.del(`party_key:${key.toLowerCase()}`);
  logger.info(`${logPrefix} Deleted party key "${key}"`);
}

export async function redisUpdatePartyKeyLobby(key: string, lobbyId: string): Promise<void> {
  const existing = await redisGetPartyKey(key);
  if (existing) {
    existing.lobbyId = lobbyId;
    await redisSavePartyKey(key, existing);
    logger.info(`${logPrefix} Updated party key "${key}" lobbyId to ${lobbyId}`);
  }
}

// --- Matchmaking Lock (atomic duplicate request guard) ---

/**
 * Attempts to acquire a matchmaking lock for a player.
 * Uses SET NX (set-if-not-exists) for atomic operation.
 * Returns true if lock was acquired, false if already locked (duplicate request).
 */
export async function redisAcquireMatchmakingLock(accountId: string): Promise<boolean> {
  const result = await redisClient.set(`matchmaking_lock:${accountId}`, "1", { NX: true, EX: 30 });
  return result === "OK";
}

/**
 * Releases the matchmaking lock for a player.
 * Called when matchmaking is cancelled or match ends.
 */
export async function redisReleaseMatchmakingLock(accountId: string): Promise<void> {
  await redisClient.del(`matchmaking_lock:${accountId}`);
}
