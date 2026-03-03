import {
  redisClient,
  RedisTeamEntry,
  RedisMatch,
  RedisMatchTicket,
  MATCH_FOUND_NOTIFICATION,
  redisOnGameplayConfigNotified,
  redisMatchMakingComplete,
  redisUpdateMatch,
} from "../config/redis";
import { logger } from "../config/logger";
import { getRandomMapByType, getMapList } from "../data/maps";
import { PlayerTesterModel } from "../database/PlayerTester";
import ObjectID from "bson-objectid";
import { randomBytes, randomInt } from "crypto";
import env from "../env/env";

const logPrefix = "[CustomLobby]:";

// --- Interfaces ---

export interface CustomLobbyPlayer {
  playerId: string;
  username: string;
  ip: string;
  ready: boolean;
  teamIndex: number; // 0 or 1
}

export interface RedisCustomLobby {
  lobbyCode: string;
  leaderId: string;
  leaderUsername: string;
  mode: "1v1" | "2v2";
  mapPool: string[]; // empty = all maps (random from full list)
  players: CustomLobbyPlayer[];
  createdAt: number;
  status: "waiting" | "starting" | "in_match";
}

export interface CustomLobbyPlayerStatus {
  playerId: string;
  username: string;
  ready: boolean;
  teamIndex: number;
  connected: boolean;     // game client connected via WebSocket
  character: string;      // current character slug from in-game
  skin: string;           // current skin slug from in-game
}

export interface CustomLobbyStatus extends Omit<RedisCustomLobby, "players"> {
  players: CustomLobbyPlayerStatus[];
}

// --- Redis Helpers ---

const LOBBY_TTL = 3600; // 1 hour
const PLAYER_LOBBY_TTL = 3600;

function lobbyKey(code: string): string {
  return `custom_lobby:${code}`;
}

function playerLobbyKey(playerId: string): string {
  return `custom_lobby_player:${playerId}`;
}

function updateChannel(code: string): string {
  return `custom_lobby_update:${code}`;
}

function matchLobbyKey(matchId: string): string {
  return `custom_lobby_match:${matchId}`;
}

async function saveLobby(lobby: RedisCustomLobby): Promise<void> {
  await redisClient.set(lobbyKey(lobby.lobbyCode), JSON.stringify(lobby), { EX: LOBBY_TTL });
}

async function getLobby(code: string): Promise<RedisCustomLobby | null> {
  const data = await redisClient.get(lobbyKey(code));
  if (!data) return null;
  return JSON.parse(data) as RedisCustomLobby;
}

async function deleteLobby(code: string): Promise<void> {
  await redisClient.del(lobbyKey(code));
}

async function setPlayerLobby(playerId: string, code: string): Promise<void> {
  await redisClient.set(playerLobbyKey(playerId), code, { EX: PLAYER_LOBBY_TTL });
}

async function getPlayerLobby(playerId: string): Promise<string | null> {
  return await redisClient.get(playerLobbyKey(playerId));
}

async function deletePlayerLobby(playerId: string): Promise<void> {
  await redisClient.del(playerLobbyKey(playerId));
}

// --- Lobby Code Generation ---

function generateLobbyCode(): string {
  const chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No I/O/0/1
  let code = "";
  for (let i = 0; i < 6; i++) {
    code += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return code;
}

// --- Public Functions ---

export async function createLobby(
  playerId: string,
  username: string,
  ip: string,
): Promise<{ lobbyCode: string } | { error: string }> {
  // Check if player is already in a lobby
  const existing = await getPlayerLobby(playerId);
  if (existing) {
    const existingLobby = await getLobby(existing);
    if (existingLobby) {
      return { error: `Already in lobby ${existing}. Leave it first.` };
    }
    // Stale key, clean it up
    await deletePlayerLobby(playerId);
  }

  // Generate unique code
  let code: string;
  do {
    code = generateLobbyCode();
  } while (await getLobby(code) !== null);

  const lobby: RedisCustomLobby = {
    lobbyCode: code,
    leaderId: playerId,
    leaderUsername: username,
    mode: "1v1",
    mapPool: [],
    players: [
      {
        playerId,
        username,
        ip,
        ready: false,
        teamIndex: 0,
      },
    ],
    createdAt: Math.floor(Date.now() / 1000),
    status: "waiting",
  };

  await saveLobby(lobby);
  await setPlayerLobby(playerId, code);

  logger.info(`${logPrefix} Lobby ${code} created by ${username} (${playerId})`);
  return { lobbyCode: code };
}

export async function joinLobby(
  lobbyCode: string,
  playerId: string,
  username: string,
  ip: string,
): Promise<{ success: boolean } | { error: string }> {
  const code = lobbyCode.toUpperCase();

  // Check if player is already in a lobby
  const existing = await getPlayerLobby(playerId);
  if (existing) {
    const existingLobby = await getLobby(existing);
    if (existingLobby) {
      if (existing === code) {
        return { success: true }; // Already in this lobby
      }
      return { error: `Already in lobby ${existing}. Leave it first.` };
    }
    await deletePlayerLobby(playerId);
  }

  const lobby = await getLobby(code);
  if (!lobby) {
    return { error: "Lobby not found." };
  }

  if (lobby.status !== "waiting") {
    return { error: "Lobby is no longer accepting players." };
  }

  // Check if already in this lobby
  if (lobby.players.some((p) => p.playerId === playerId)) {
    await setPlayerLobby(playerId, code);
    return { success: true };
  }

  // Check max players for mode
  const maxPlayers = lobby.mode === "1v1" ? 2 : 4;
  if (lobby.players.length >= maxPlayers) {
    return { error: `Lobby is full (${maxPlayers} players max for ${lobby.mode}).` };
  }

  // Assign to team with fewer players
  const team0Count = lobby.players.filter((p) => p.teamIndex === 0).length;
  const team1Count = lobby.players.filter((p) => p.teamIndex === 1).length;
  const teamIndex = team0Count <= team1Count ? 0 : 1;

  lobby.players.push({
    playerId,
    username,
    ip,
    ready: false,
    teamIndex,
  });

  await saveLobby(lobby);
  await setPlayerLobby(playerId, code);
  await publishUpdate(code);

  logger.info(`${logPrefix} ${username} (${playerId}) joined lobby ${code}`);
  return { success: true };
}

export async function leaveLobby(
  playerId: string,
): Promise<{ success: boolean } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) {
    return { error: "Not in a lobby." };
  }

  const lobby = await getLobby(code);
  if (!lobby) {
    await deletePlayerLobby(playerId);
    return { error: "Lobby not found." };
  }

  // Remove player
  lobby.players = lobby.players.filter((p) => p.playerId !== playerId);
  await deletePlayerLobby(playerId);

  if (lobby.players.length === 0) {
    // Delete empty lobby
    await deleteLobby(code);
    // Publish empty update so SSE clients know
    await redisClient.publish(updateChannel(code), JSON.stringify({ deleted: true, lobbyCode: code }));
    logger.info(`${logPrefix} Lobby ${code} deleted (empty)`);
    return { success: true };
  }

  // Transfer leadership if leader left
  if (lobby.leaderId === playerId) {
    lobby.leaderId = lobby.players[0].playerId;
    lobby.leaderUsername = lobby.players[0].username;
    logger.info(`${logPrefix} Leadership transferred to ${lobby.leaderUsername} in lobby ${code}`);
  }

  await saveLobby(lobby);
  await publishUpdate(code);

  logger.info(`${logPrefix} Player ${playerId} left lobby ${code}`);
  return { success: true };
}

export async function switchTeam(
  playerId: string,
): Promise<{ success: boolean } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { error: "Not in a lobby." };

  const lobby = await getLobby(code);
  if (!lobby) return { error: "Lobby not found." };

  const player = lobby.players.find((p) => p.playerId === playerId);
  if (!player) return { error: "Not in this lobby." };

  const newTeam = player.teamIndex === 0 ? 1 : 0;

  // Check if target team is full
  const playersPerTeam = lobby.mode === "1v1" ? 1 : 2;
  const targetTeamCount = lobby.players.filter((p) => p.teamIndex === newTeam).length;
  if (targetTeamCount >= playersPerTeam) {
    return { error: `Team ${newTeam + 1} is full.` };
  }

  player.teamIndex = newTeam;
  player.ready = false; // Reset ready on team switch

  await saveLobby(lobby);
  await publishUpdate(code);

  return { success: true };
}

export async function toggleReady(
  playerId: string,
): Promise<{ success: boolean; ready: boolean } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { error: "Not in a lobby." };

  const lobby = await getLobby(code);
  if (!lobby) return { error: "Lobby not found." };

  const player = lobby.players.find((p) => p.playerId === playerId);
  if (!player) return { error: "Not in this lobby." };

  player.ready = !player.ready;

  await saveLobby(lobby);
  await publishUpdate(code);

  return { success: true, ready: player.ready };
}

export async function setMapPool(
  playerId: string,
  maps: string[],
): Promise<{ success: boolean } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { error: "Not in a lobby." };

  const lobby = await getLobby(code);
  if (!lobby) return { error: "Lobby not found." };

  if (lobby.leaderId !== playerId) {
    return { error: "Only the lobby leader can set the map pool." };
  }

  // Validate all maps exist in the mode's map list
  if (maps.length > 0) {
    const validMaps = getMapList(lobby.mode);
    const validIds = new Set(validMaps.map((m) => m.id));
    for (const map of maps) {
      if (!validIds.has(map)) {
        return { error: `Invalid map for this mode: ${map}` };
      }
    }
  }

  lobby.mapPool = maps;

  await saveLobby(lobby);
  await publishUpdate(code);

  return { success: true };
}

export async function selectMode(
  playerId: string,
  mode: "1v1" | "2v2",
): Promise<{ success: boolean } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { error: "Not in a lobby." };

  const lobby = await getLobby(code);
  if (!lobby) return { error: "Lobby not found." };

  if (lobby.leaderId !== playerId) {
    return { error: "Only the lobby leader can change the mode." };
  }

  if (mode !== "1v1" && mode !== "2v2") {
    return { error: "Invalid mode. Must be 1v1 or 2v2." };
  }

  const maxPlayers = mode === "1v1" ? 2 : 4;
  if (lobby.players.length > maxPlayers) {
    return { error: `Too many players for ${mode}. Remove players first.` };
  }

  lobby.mode = mode;
  lobby.mapPool = []; // Reset map pool on mode change
  // Reset all ready states
  for (const p of lobby.players) {
    p.ready = false;
  }

  await saveLobby(lobby);
  await publishUpdate(code);

  return { success: true };
}

export async function startMatch(
  playerId: string,
): Promise<{ success: boolean; matchId: string } | { error: string }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { error: "Not in a lobby." };

  const lobby = await getLobby(code);
  if (!lobby) return { error: "Lobby not found." };

  if (lobby.leaderId !== playerId) {
    return { error: "Only the lobby leader can start the match." };
  }

  if (lobby.status !== "waiting") {
    return { error: "Match already starting." };
  }

  // Validate team composition
  const playersPerTeam = lobby.mode === "1v1" ? 1 : 2;
  const team0 = lobby.players.filter((p) => p.teamIndex === 0);
  const team1 = lobby.players.filter((p) => p.teamIndex === 1);

  if (team0.length !== playersPerTeam || team1.length !== playersPerTeam) {
    return { error: `Need exactly ${playersPerTeam} player(s) per team for ${lobby.mode}.` };
  }

  // Check all players are ready
  const notReady = lobby.players.filter((p) => !p.ready);
  if (notReady.length > 0) {
    return { error: `Not all players are ready: ${notReady.map((p) => p.username).join(", ")}` };
  }

  // Check all game clients are connected
  for (const p of lobby.players) {
    const isOnline = await redisClient.sIsMember("online_players", p.playerId);
    if (!isOnline) {
      return { error: `${p.username}'s game client is not connected.` };
    }
  }

  // Sync character/skin → connections:{id} hash so WebSocket can read them
  // (Regular matchmaking does this in matches.ts before queueing; custom lobby skips that flow)
  // Fallback chain: player:{id} → connections:{id} → MongoDB
  for (const p of lobby.players) {
    try {
      let character = "";
      let skin = "";
      let profileIcon = "";

      // Try player:{id} hash first
      const playerData = await redisClient.hGetAll(`player:${p.playerId}`);
      character = playerData?.character || "";
      skin = playerData?.skin || "";
      profileIcon = playerData?.profileIcon || "";

      // Fallback to connections:{id} (might already have it from a previous match)
      if (!character) {
        const connData = await redisClient.hGetAll(`connections:${p.playerId}`);
        character = connData?.character || "";
        skin = connData?.skin || "";
        profileIcon = connData?.profileIcon || profileIcon;
      }

      // Fallback to MongoDB
      if (!character) {
        const dbPlayer = await PlayerTesterModel.findById(p.playerId).lean();
        if (dbPlayer) {
          character = (dbPlayer as any).character || "";
          skin = (dbPlayer as any).variant || "";
        }
      }

      if (character && skin) {
        const update: Record<string, string> = { character, skin };
        if (profileIcon) update.profileIcon = profileIcon;
        await redisClient.hSet(`connections:${p.playerId}`, update);
        await redisClient.hSet(`connections:${p.ip}`, update);
      }
    } catch (e) {
      logger.warn(`${logPrefix} Failed to sync character/skin for player ${p.playerId}: ${e}`);
    }
  }

  // Mark as starting
  lobby.status = "starting";
  await saveLobby(lobby);
  await publishUpdate(code);

  try {
    // Create the match (replicating matchmaking-worker.ts createMatch)
    const matchId = ObjectID().toHexString();
    const resultId = ObjectID().toHexString();
    const matchmakingRequestId = ObjectID().toHexString();

    // Build RedisTeamEntry[] from lobby teams
    const allPlayers = [...team0, ...team1];
    const randomHost = Math.floor(Math.random() * allPlayers.length);

    const players: RedisTeamEntry[] = allPlayers.map((p, idx) => ({
      playerId: p.playerId,
      partyId: matchId,
      playerIndex: idx,
      teamIndex: p.teamIndex as 0 | 1,
      isHost: idx === randomHost,
      ip: p.ip,
    }));

    // Build a ticket so the perk-locking handler can find playerIds
    const ticket: RedisMatchTicket = {
      party_size: allPlayers.length,
      players: allPlayers.map((p) => ({
        id: p.playerId,
        skill: 0,
        region: "local",
        partyId: matchId,
      })),
      created_at: Date.now(),
      partyId: matchId,
      matchmakingRequestId,
      isPasswordMatch: true,
    };

    // Store match in Redis
    const match: RedisMatch = {
      matchId,
      resultId,
      tickets: [ticket],
      status: "pending",
      createdAt: Date.now(),
      matchType: lobby.mode,
      totalPlayers: allPlayers.length,
      rollbackPort: randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH),
      isPasswordMatch: true, // Custom lobby = no ELO
    };
    await redisUpdateMatch(matchId, match);

    // Select map from pool (or random from all maps if pool is empty)
    const map = lobby.mapPool.length > 0
      ? lobby.mapPool[Math.floor(Math.random() * lobby.mapPool.length)]
      : getRandomMapByType(lobby.mode);

    // Build notification
    const notification: MATCH_FOUND_NOTIFICATION = {
      players,
      matchId,
      matchKey: randomBytes(32).toString("base64"),
      map,
      mode: lobby.mode,
      rollbackPort: randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH),
    };

    // Publish to same 3 channels as matchmaking worker
    const playerIds = players.map((p) => p.playerId);
    await redisOnGameplayConfigNotified(notification);
    await redisMatchMakingComplete(matchId, matchmakingRequestId, playerIds);
    // redisGameServerInstanceReady is triggered by /ovs_register after rollback server fetches match config

    // Update lobby status
    lobby.status = "in_match";
    await saveLobby(lobby);
    await publishUpdate(code);

    // Track matchId → lobbyCode so we can handle rematch on match end
    await redisClient.set(matchLobbyKey(matchId), code, { EX: 1200 }); // 20 min TTL

    logger.info(`${logPrefix} Match ${matchId} started from lobby ${code} on map ${map} (${lobby.mode})`);

    return { success: true, matchId };
  } catch (e) {
    // Revert status on error
    lobby.status = "waiting";
    await saveLobby(lobby);
    await publishUpdate(code);
    logger.error(`${logPrefix} Error starting match from lobby ${code}: ${e}`);
    return { error: "Failed to start match. Try again." };
  }
}

/**
 * Get full lobby status including connection status and character info for each player.
 */
export async function getLobbyWithStatus(lobbyCode: string): Promise<CustomLobbyStatus | null> {
  const lobby = await getLobby(lobbyCode.toUpperCase());
  if (!lobby) return null;

  const players: CustomLobbyPlayerStatus[] = await Promise.all(
    lobby.players.map(async (p) => {
      // Check game client connection
      const connected = await redisClient.sIsMember("online_players", p.playerId);

      // Get character/skin — try multiple sources:
      // 1. player:{id} hash (set by lock_lobby_loadout SSC)
      // 2. connections:{id} hash (set during matchmaking queue)
      // 3. MongoDB PlayerTester (stores last used character)
      let character = "";
      let skin = "";
      try {
        const playerData = await redisClient.hGetAll(`player:${p.playerId}`);
        character = playerData?.character || "";
        skin = playerData?.skin || "";

        // Fallback to connections hash
        if (!character) {
          const connData = await redisClient.hGetAll(`connections:${p.playerId}`);
          character = connData?.character || "";
          skin = connData?.skin || "";
        }

        // Fallback to MongoDB
        if (!character) {
          const dbPlayer = await PlayerTesterModel.findById(p.playerId).lean();
          if (dbPlayer) {
            character = (dbPlayer as any).character || "";
            skin = (dbPlayer as any).variant || "";
          }
        }
      } catch {
        // ignore read errors
      }

      return {
        playerId: p.playerId,
        username: p.username,
        ready: p.ready,
        teamIndex: p.teamIndex,
        connected,
        character,
        skin,
      };
    }),
  );

  return {
    lobbyCode: lobby.lobbyCode,
    leaderId: lobby.leaderId,
    leaderUsername: lobby.leaderUsername,
    mode: lobby.mode,
    mapPool: lobby.mapPool,
    players,
    createdAt: lobby.createdAt,
    status: lobby.status,
  };
}

/**
 * Publish a lobby state update to SSE listeners via Redis pub/sub.
 */
export async function publishUpdate(lobbyCode: string): Promise<void> {
  const status = await getLobbyWithStatus(lobbyCode);
  if (status) {
    await redisClient.publish(updateChannel(lobbyCode), JSON.stringify(status));
  }
}

// ============================================================
// Rematch System
// ============================================================

/**
 * Check if a matchId belongs to a custom lobby.
 * Returns the lobby code if it does, null otherwise.
 */
export async function getCustomLobbyForMatch(matchId: string): Promise<string | null> {
  return await redisClient.get(matchLobbyKey(matchId));
}

/**
 * Called when a custom lobby match ends.
 * Resets the lobby to "waiting" status, unreadies everyone,
 * and schedules auto-rematch after 25 seconds.
 */
export async function handleCustomLobbyMatchEnd(matchId: string): Promise<void> {
  const code = await getCustomLobbyForMatch(matchId);
  if (!code) return;

  const lobby = await getLobby(code);
  if (!lobby) return;

  // Reset lobby for rematch
  lobby.status = "waiting";
  for (const p of lobby.players) {
    p.ready = false;
  }
  await saveLobby(lobby);
  await publishUpdate(code);

  logger.info(`${logPrefix} Match ${matchId} ended for lobby ${code}, waiting for rematch (25s timer)`);

  // Clean up the match→lobby mapping
  await redisClient.del(matchLobbyKey(matchId));

  // Schedule auto-rematch after 25 seconds (game's RematchTimeoutSeconds)
  // Store the timer key so decline can cancel it
  const rematchTimerKey = `custom_lobby_rematch_timer:${code}`;
  await redisClient.set(rematchTimerKey, matchId, { EX: 30 });

  setTimeout(async () => {
    try {
      // Check if rematch was cancelled (lobby deleted or all declined)
      const timerStillActive = await redisClient.get(rematchTimerKey);
      if (!timerStillActive) {
        logger.info(`${logPrefix} Rematch timer cancelled for lobby ${code}`);
        return;
      }
      await redisClient.del(rematchTimerKey);
      await redisClient.del(`custom_lobby_rematch_accept:${code}`);
      await triggerRematch(code);
    } catch (e) {
      logger.error(`${logPrefix} Error in rematch timer for lobby ${code}: ${e}`);
    }
  }, 25000);
}

/**
 * Called when a player accepts rematch from the post-match screen.
 * Tracks acceptance per player. When ALL players accept, immediately triggers rematch
 * (cancels the 25-second timer).
 */
export async function handleRematchAccept(playerId: string): Promise<void> {
  const code = await getPlayerLobby(playerId);
  if (!code) return;

  const lobby = await getLobby(code);
  if (!lobby) return;

  const rematchTimerKey = `custom_lobby_rematch_timer:${code}`;
  const timerActive = await redisClient.get(rematchTimerKey);
  if (!timerActive) return; // No pending rematch

  // Track this player's acceptance
  const acceptKey = `custom_lobby_rematch_accept:${code}`;
  await redisClient.sAdd(acceptKey, playerId);
  await redisClient.expire(acceptKey, 30); // TTL matches timer

  logger.info(`${logPrefix} Player ${playerId} accepted rematch in lobby ${code}`);

  // Check if ALL players in the lobby have accepted
  const acceptedCount = await redisClient.sCard(acceptKey);
  if (acceptedCount >= lobby.players.length) {
    logger.info(`${logPrefix} All ${lobby.players.length} players accepted rematch in lobby ${code} — triggering immediately`);

    // Cancel the 25-second timer by removing the key
    await redisClient.del(rematchTimerKey);
    await redisClient.del(acceptKey);

    // Trigger rematch immediately
    await triggerRematch(code);
  }
}

/**
 * Called when any player declines rematch from the post-match screen.
 * All-or-none: if anyone declines, everyone goes back to menus.
 * The lobby stays alive — players can rematch from the web page.
 * Returns all player IDs so the caller can send RematchDeclinedNotification to everyone.
 */
export async function handleRematchDecline(playerId: string): Promise<{ playerIds: string[] | null; lobbyCode: string | null }> {
  const code = await getPlayerLobby(playerId);
  if (!code) return { playerIds: null, lobbyCode: null };

  const lobby = await getLobby(code);
  if (!lobby) return { playerIds: null, lobbyCode: null };

  logger.info(`${logPrefix} Player ${playerId} declined rematch in lobby ${code} — cancelling rematch for all`);

  // Cancel the rematch timer and clean up acceptance tracking
  await redisClient.del(`custom_lobby_rematch_timer:${code}`);
  await redisClient.del(`custom_lobby_rematch_accept:${code}`);

  // Get all player IDs so WebSocket can send RematchDeclinedNotification to everyone
  const allPlayerIds = lobby.players.map((p) => p.playerId);

  // Lobby stays alive in "waiting" status — players can rematch from web page
  lobby.status = "waiting";
  for (const p of lobby.players) {
    p.ready = false;
  }
  await saveLobby(lobby);
  await publishUpdate(code);

  return { playerIds: allPlayerIds, lobbyCode: code };
}

/**
 * Auto-start a new match for the custom lobby (called after rematch timeout).
 * Skips the ready check — everyone who didn't decline is in.
 */
async function triggerRematch(lobbyCode: string): Promise<void> {
  const lobby = await getLobby(lobbyCode);
  if (!lobby) return;

  if (lobby.status !== "waiting") {
    logger.info(`${logPrefix} Lobby ${lobbyCode} not in waiting state for rematch, skipping`);
    return;
  }

  // Check team composition
  const playersPerTeam = lobby.mode === "1v1" ? 1 : 2;
  const team0 = lobby.players.filter((p) => p.teamIndex === 0);
  const team1 = lobby.players.filter((p) => p.teamIndex === 1);

  if (team0.length !== playersPerTeam || team1.length !== playersPerTeam) {
    logger.info(`${logPrefix} Not enough players for rematch in lobby ${lobbyCode}, teams: ${team0.length}v${team1.length}`);
    // Publish update so web clients know rematch didn't happen
    await publishUpdate(lobbyCode);
    return;
  }

  // Check all game clients are still connected
  for (const p of lobby.players) {
    const isOnline = await redisClient.sIsMember("online_players", p.playerId);
    if (!isOnline) {
      logger.info(`${logPrefix} Player ${p.username} disconnected, skipping rematch for lobby ${lobbyCode}`);
      await publishUpdate(lobbyCode);
      return;
    }
  }

  // Sync character/skin → connections:{id} hash for rematch (same fallback chain)
  for (const p of lobby.players) {
    try {
      let character = "";
      let skin = "";
      let profileIcon = "";

      const playerData = await redisClient.hGetAll(`player:${p.playerId}`);
      character = playerData?.character || "";
      skin = playerData?.skin || "";
      profileIcon = playerData?.profileIcon || "";

      if (!character) {
        const connData = await redisClient.hGetAll(`connections:${p.playerId}`);
        character = connData?.character || "";
        skin = connData?.skin || "";
        profileIcon = connData?.profileIcon || profileIcon;
      }

      if (!character) {
        const dbPlayer = await PlayerTesterModel.findById(p.playerId).lean();
        if (dbPlayer) {
          character = (dbPlayer as any).character || "";
          skin = (dbPlayer as any).variant || "";
        }
      }

      if (character && skin) {
        const update: Record<string, string> = { character, skin };
        if (profileIcon) update.profileIcon = profileIcon;
        await redisClient.hSet(`connections:${p.playerId}`, update);
        await redisClient.hSet(`connections:${p.ip}`, update);
      }
    } catch (e) {
      logger.warn(`${logPrefix} Failed to sync character/skin for rematch player ${p.playerId}: ${e}`);
    }
  }

  // Mark as starting
  lobby.status = "starting";
  await saveLobby(lobby);
  await publishUpdate(lobbyCode);

  try {
    const matchId = ObjectID().toHexString();
    const resultId = ObjectID().toHexString();
    const matchmakingRequestId = ObjectID().toHexString();

    const allPlayers = [...team0, ...team1];
    const randomHost = Math.floor(Math.random() * allPlayers.length);

    const players: RedisTeamEntry[] = allPlayers.map((p, idx) => ({
      playerId: p.playerId,
      partyId: matchId,
      playerIndex: idx,
      teamIndex: p.teamIndex as 0 | 1,
      isHost: idx === randomHost,
      ip: p.ip,
    }));

    const ticket: RedisMatchTicket = {
      party_size: allPlayers.length,
      players: allPlayers.map((p) => ({
        id: p.playerId,
        skill: 0,
        region: "local",
        partyId: matchId,
      })),
      created_at: Date.now(),
      partyId: matchId,
      matchmakingRequestId,
      isPasswordMatch: true,
    };

    const match: RedisMatch = {
      matchId,
      resultId,
      tickets: [ticket],
      status: "pending",
      createdAt: Date.now(),
      matchType: lobby.mode,
      totalPlayers: allPlayers.length,
      rollbackPort: randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH),
      isPasswordMatch: true,
    };
    await redisUpdateMatch(matchId, match);

    // Select map from pool (or random from all maps if pool is empty)
    const map = lobby.mapPool.length > 0
      ? lobby.mapPool[Math.floor(Math.random() * lobby.mapPool.length)]
      : getRandomMapByType(lobby.mode);

    const notification: MATCH_FOUND_NOTIFICATION = {
      players,
      matchId,
      matchKey: randomBytes(32).toString("base64"),
      map,
      mode: lobby.mode,
      rollbackPort: randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH),
    };

    const playerIds = players.map((p) => p.playerId);
    await redisOnGameplayConfigNotified(notification);
    await redisMatchMakingComplete(matchId, matchmakingRequestId, playerIds);
    // redisGameServerInstanceReady is triggered by /ovs_register after rollback server fetches match config

    lobby.status = "in_match";
    await saveLobby(lobby);
    await publishUpdate(lobbyCode);

    // Track matchId → lobbyCode for next rematch cycle
    await redisClient.set(matchLobbyKey(matchId), lobbyCode, { EX: 1200 });

    logger.info(`${logPrefix} Rematch ${matchId} started for lobby ${lobbyCode} on map ${map}`);
  } catch (e) {
    lobby.status = "waiting";
    await saveLobby(lobby);
    await publishUpdate(lobbyCode);
    logger.error(`${logPrefix} Error triggering rematch for lobby ${lobbyCode}: ${e}`);
  }
}
