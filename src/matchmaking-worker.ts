import {
  redisClient,
  RedisMatchTicket,
  ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL,
  MATCHMAKING_COMPLETE_CHANNEL,
  RedisMatch,
  RedisTeamEntry,
  MATCH_FOUND_NOTIFICATION,
  redisMatchMakingComplete,
  redisPopMatchTicketsFromQueue,
  redisGetPlayer,
  redisGetBlockedPlayers,
  redisSetBlockedPlayers,
  redisUpdateMatch,
  redisOnGameplayConfigNotified,
  redisGetMatchTickets,
} from "./config/redis";
import { logger } from "./config/logger";
import ObjectID from "bson-objectid";
import { randomBytes } from "crypto";
import { MATCH_TYPES, getBaseMode } from "./services/matchmakingService";
import { getRandomMapByType } from "./data/maps";
import { randomUUID, randomInt } from "crypto";
import { IDeployInfo, DeployInfo, getDefaultDeployInfo, useOnDemandRollback } from "./services/rollbackService";
import { resolveAccountByIdentifiers } from "./services/identityService";
import env from "./env/env";

const serviceName = "MatchmakingWorker";
const logPrefix = `[${serviceName}]:`;
const CHECK_INTERVAL_MS = 2000;
const LOCK_TTL_SECONDS = 10; // Auto-expire lock if worker crashes
const workerId = `worker_${process.pid}_${Date.now()}`;

async function playersBlockedEachOther(playerIds: string[]): Promise<{result: boolean, blockedPairs?: [string, string][]}> {
  const blockedPairs: [string, string][] = [];
  for (const playerId of playerIds) {
    const blocked = await redisGetBlockedPlayers(playerId);
    for (const b of blocked) {
      if (playerIds.includes(b)) {
        blockedPairs.push([playerId, b]);
      }
    }
  }
  if (blockedPairs.length > 0) {
    return { result: true, blockedPairs };
  }
  return { result: false };
}

/**
 * Acquire a distributed lock via Redis SET NX EX.
 * Returns true if lock acquired, false if another worker holds it.
 */
async function acquireLock(queueKey: string): Promise<boolean> {
  const lockKey = `matchmaking:lock:${queueKey}`;
  const result = await redisClient.set(lockKey, workerId, { NX: true, EX: LOCK_TTL_SECONDS });
  return result === "OK";
}

/**
 * Release a distributed lock. Only releases if we own it (prevents releasing another worker's lock).
 */
async function releaseLock(queueKey: string): Promise<void> {
  const lockKey = `matchmaking:lock:${queueKey}`;
  const owner = await redisClient.get(lockKey);
  if (owner === workerId) {
    await redisClient.del(lockKey);
  }
}

const MATCH_RULES = {
  "1v1": {
    playersPerTeam: 1,
    teamsRequired: 2,
    totalPlayersRequired: 2,
  },
  "2v2": {
    playersPerTeam: 2,
    teamsRequired: 2,
    totalPlayersRequired: 4,
  },
};

// --- ELO Range Configuration ---
// Time-based expanding ELO range for skill-based matchmaking

function getEloRange(ticketAgeSec: number): number {
  if (ticketAgeSec < 5) {
    // 0-5 seconds: ~1 tier range
    return 250;
  } else if (ticketAgeSec < 10) {
    // 5-10 seconds: ~2 tier range
    return 500;
  } else {
    // 10+ seconds: match anyone
    return Infinity;
  }
}

/**
 * Calculate the average skill (ELO) for a ticket's players.
 */
function getTicketAvgSkill(ticket: RedisMatchTicket): number {
  if (ticket.players.length === 0) return 0;
  const total = ticket.players.reduce((sum, p) => sum + p.skill, 0);
  return total / ticket.players.length;
}

/**
 * Check if two tickets share any player IDs (dedup guard).
 * Returns true if they have overlapping players.
 */
function ticketsSharePlayers(ticketA: RedisMatchTicket, ticketB: RedisMatchTicket): boolean {
  const idsA = new Set(ticketA.players.map((p) => p.id));
  return ticketB.players.some((p) => idsA.has(p.id));
}

/**
 * Remove duplicate tickets from a queue — if the same player appears
 * in multiple tickets, keep only the newest one and remove the stale ones.
 */
async function deduplicateQueue(queueKey: string, tickets: RedisMatchTicket[]): Promise<RedisMatchTicket[]> {
  const seen = new Map<string, number>(); // playerId → index of ticket
  const staleIndices = new Set<number>();

  for (let i = 0; i < tickets.length; i++) {
    for (const player of tickets[i].players) {
      if (seen.has(player.id)) {
        // This player already has a ticket — mark the older one as stale
        const prevIdx = seen.get(player.id)!;
        const prevTicket = tickets[prevIdx];
        const currTicket = tickets[i];
        if (currTicket.created_at >= prevTicket.created_at) {
          staleIndices.add(prevIdx);
          seen.set(player.id, i);
        } else {
          staleIndices.add(i);
        }
      } else {
        seen.set(player.id, i);
      }
    }
  }

  if (staleIndices.size > 0) {
    const staleTickets = Array.from(staleIndices).map((i) => tickets[i]);
    logger.warn(
      `${logPrefix} Removing ${staleTickets.length} duplicate ticket(s) from ${queueKey} for players: ` +
      staleTickets.flatMap((t) => t.players.map((p) => p.id)).join(", "),
    );
    await redisPopMatchTicketsFromQueue(queueKey, staleTickets);
    return tickets.filter((_, i) => !staleIndices.has(i));
  }

  return tickets;
}

/**
 * Check if two tickets are within ELO range of each other.
 * Uses the wider range of the two (the one that's been waiting longer).
 */
function areTicketsInRange(ticketA: RedisMatchTicket, ticketB: RedisMatchTicket, nowSec: number): boolean {
  const ageA = nowSec - ticketA.created_at; // both in seconds
  const ageB = nowSec - ticketB.created_at;
  // Both players must be within range — use the stricter (shorter waiting) range
  const range = Math.min(getEloRange(ageA), getEloRange(ageB));
  const skillDiff = Math.abs(getTicketAvgSkill(ticketA) - getTicketAvgSkill(ticketB));
  return skillDiff <= range;
}

export function startMatchMakingWorker(): void {
  logger.info(`${logPrefix} Starting matchmaking worker...`);
  // Run the first check immediately
  checkQueues();
  // Then set up interval to check regularly
  setInterval(checkQueues, CHECK_INTERVAL_MS);

  logger.info(`${logPrefix} Matchmaking worker started, checking queue every ${CHECK_INTERVAL_MS}ms`);
}

// Process 1v1 matchmaking queue with ELO-based matching
async function process1v1Queue(queueKey: string = MATCH_TYPES.ONE_V_ONE): Promise<boolean> {
  if (!await acquireLock(queueKey)) return false;
  try {
    // Get all tickets in the queue
    let tickets = await redisGetMatchTickets(queueKey);

    if (tickets.length < MATCH_RULES["1v1"].teamsRequired) {
      return false; // Not enough tickets to make a match
    }

    // Remove duplicate tickets (same player in queue twice from stale data)
    tickets = await deduplicateQueue(queueKey, tickets);

    if (tickets.length < MATCH_RULES["1v1"].teamsRequired) {
      return false;
    }

    logger.trace(`${logPrefix} Found ${tickets.length} tickets in ${queueKey} queue, attempting to create a match`);

    // Filter to solo players only
    const soloTickets = tickets.filter((t) => t.party_size === 1);

    if (soloTickets.length < MATCH_RULES["1v1"].teamsRequired) {
      logger.trace(`${logPrefix} Not enough solo tickets for a 1v1 match in ${queueKey} (need 2, found ${soloTickets.length})`);
      return false;
    }

    // ELO-based matching: sort by age (oldest first), find compatible pairs
    const now = Math.floor(Date.now() / 1000); // seconds to match MVSTime format
    const sortedTickets = soloTickets.sort((a, b) => a.created_at - b.created_at);

    for (let i = 0; i < sortedTickets.length; i++) {
      const ticketA = sortedTickets[i];
      for (let j = i + 1; j < sortedTickets.length; j++) {
        const ticketB = sortedTickets[j];
        // Never match a player against themselves
        if (ticketsSharePlayers(ticketA, ticketB)) continue;
        if (areTicketsInRange(ticketA, ticketB, now)) {
          const matchedTickets = [ticketA, ticketB];
          try {
            //const allPlayers = matchedTickets.flatMap((t) => t.players.map((p) => p.id)).join(", ");
            //const shouldMakeMatch = await playersBlockedEachOther(allPlayers.split(", "));
            const allPlayers = matchedTickets.flatMap(t => t.players.map(p => p.id));
            const shouldMakeMatch = await playersBlockedEachOther(allPlayers);
            if (shouldMakeMatch.result) {
              const blockedPlayer1 = shouldMakeMatch.blockedPairs![0][0];
              const blockedPlayer1Username = (await resolveAccountByIdentifiers({ accountId: blockedPlayer1 }))?.username || blockedPlayer1;
              const blockedPlayer2 = shouldMakeMatch.blockedPairs![0][1];
              const blockedPlayer2Username = (await resolveAccountByIdentifiers({ accountId: blockedPlayer2 }))?.username || blockedPlayer2;
              logger.warn(`${logPrefix} Cannot match players for matchmakingRequestIds ${ticketA.matchmakingRequestId} & ${ticketB.matchmakingRequestId} due to player blocks between: ${blockedPlayer1} (${blockedPlayer1Username}) & ${blockedPlayer2} (${blockedPlayer2Username})`);
              continue;
            }
            await redisPopMatchTicketsFromQueue(queueKey, matchedTickets);
            await createMatch(matchedTickets, getBaseMode(queueKey));
            logger.info(
              `${logPrefix} ELO matched: ${getTicketAvgSkill(ticketA)} vs ${getTicketAvgSkill(ticketB)} ` +
              `(range: ${getEloRange(now - ticketA.created_at)}/${getEloRange(now - ticketB.created_at)})`,
            );
            return true;
          } catch (error) {
            logger.error(`${logPrefix} Error removing matched tickets from queue: ${error}`);
            return false;
          }
        }
      }
    }

    logger.trace(`${logPrefix} No ELO-compatible 1v1 match found in ${queueKey} yet (${soloTickets.length} waiting)`);
    return false;
  }
  catch (error) {
    logger.error(`${logPrefix} Error processing 1v1 queue ${queueKey}: ${error}`);
    return false;
  } finally {
    await releaseLock(queueKey);
  }
}

// Process 2v2 matchmaking queue with ELO-based matching
async function process2v2Queue(queueKey: string = MATCH_TYPES.TWO_V_TWO): Promise<boolean> {
  if (!await acquireLock(queueKey)) return false;
  try {
    // Get all tickets in the queue
    let tickets = await redisGetMatchTickets(queueKey);

    // Remove duplicate tickets (same player in queue twice from stale data)
    tickets = await deduplicateQueue(queueKey, tickets);

    // Count total players across all tickets (a party of 2 is 1 ticket with 2 players)
    const totalPlayersInQueue = tickets.reduce((sum, t) => sum + t.players.length, 0);

    if (totalPlayersInQueue < MATCH_RULES["2v2"].totalPlayersRequired) {
      return false; // Not enough players to make a match
    }

    logger.trace(`${logPrefix} Found ${tickets.length} tickets (${totalPlayersInQueue} players) in ${queueKey} queue, attempting to create a match`);

    const now = Math.floor(Date.now() / 1000);

    // Sort all tickets by age (oldest first) — no composition priority.
    // Whoever has been waiting longest gets matched first, whether solo or duo.
    const sorted = tickets.sort((a, b) => a.created_at - b.created_at);

    // Greedy: start with the oldest ticket, accumulate compatible tickets until we have 4 players.
    // Constraint: parties stay together (a duo is 1 ticket with 2 players).
    for (let i = 0; i < sorted.length; i++) {
      const candidates: RedisMatchTicket[] = [sorted[i]];
      let playerCount = sorted[i].players.length;

      for (let j = i + 1; j < sorted.length && playerCount < 4; j++) {
        if (ticketsSharePlayers(sorted[i], sorted[j])) continue;

        // Check ELO: compare candidate team avg vs existing candidates avg
        const existingAvg = candidates.reduce((sum, t) => sum + getTicketAvgSkill(t), 0) / candidates.length;
        const candidateAvg = getTicketAvgSkill(sorted[j]);
        const ageMax = Math.max(...candidates.map(t => now - t.created_at), now - sorted[j].created_at);
        const range = getEloRange(ageMax);

        if (Math.abs(existingAvg - candidateAvg) <= range) {
          // Don't exceed 4 players
          if (playerCount + sorted[j].players.length <= 4) {
            candidates.push(sorted[j]);
            playerCount += sorted[j].players.length;
          }
        }
      }

      if (playerCount === 4) {
        try {
          const composition = candidates.map(t => t.players.length).join("+");
          const avgSkills = candidates.map(t => Math.round(getTicketAvgSkill(t)));
          logger.info(`${logPrefix} ELO matched ${composition}: skills [${avgSkills.join(", ")}]`);

          // const allPlayers = candidates.flatMap((t) => t.players.map((p) => p.id)).join(", ");
          // const shouldMakeMatch = await playersBlockedEachOther(allPlayers.split(", "));
          const allPlayers = candidates.flatMap((t) => t.players.map((p) => p.id));
          const shouldMakeMatch = await playersBlockedEachOther(allPlayers);
          if (shouldMakeMatch.result) {
            const blockedPlayer1 = shouldMakeMatch.blockedPairs![0][0];
            const blockedPlayer1Username = (await resolveAccountByIdentifiers({ accountId: blockedPlayer1 }))?.username || blockedPlayer1;
            const blockedPlayer2 = shouldMakeMatch.blockedPairs![0][1];
            const blockedPlayer2Username = (await resolveAccountByIdentifiers({ accountId: blockedPlayer2 }))?.username || blockedPlayer2;

            logger.warn(`${logPrefix} Cannot match players for matchmakingRequestIds ${candidates.map(t => t.matchmakingRequestId).join(", ")} due to player blocks between: ${blockedPlayer1} (${blockedPlayer1Username}) & ${blockedPlayer2} (${blockedPlayer2Username})`);
            continue;
          }

          await redisPopMatchTicketsFromQueue(queueKey, candidates);
          await createMatch(candidates, getBaseMode(queueKey));
          return true;
        } catch (error) {
          logger.error(`${logPrefix} Error removing matched tickets from ${queueKey}: ${error}`);
          return false;
        }
      }
    }

    const duoCount = tickets.filter(t => t.players.length >= 2).length;
    const soloCount = tickets.filter(t => t.players.length === 1).length;
    logger.trace(
      `${logPrefix} No ELO-compatible 2v2 match found in ${queueKey} yet ` +
      `(${totalPlayersInQueue} players — ${duoCount} duo(s), ${soloCount} solo(s))`,
    );
    return false;
  }
  catch (error) {
    logger.error(`${logPrefix} Error processing 2v2 queue ${queueKey}: ${error}`);
    return false;
  } finally {
    await releaseLock(queueKey);
  }
}

// ChatGPT came up with this o.o
export async function createTeams(tickets: RedisMatchTicket[]): Promise<RedisTeamEntry[]> {
  // 1. total number of players
  const totalPlayers = tickets.reduce((sum, t) => sum + t.players.length, 0);
  if (totalPlayers % 2 !== 0) {
    //throw new Error("Need an even number of total players");
    logger.warn(`${logPrefix} Total players is odd (${totalPlayers}), one player will be left without a team`);
  }
  const slotsPerTeam = totalPlayers / 2;

  // 2. shuffle parties (Fisher–Yates)
  const shuffled = tickets.slice();
  for (let i = shuffled.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [
      shuffled[i],
      shuffled[j],
    ] = [
      shuffled[j],
      shuffled[i],
    ];
  }

  // 3. assign each party to team 0 or 1
  const usedSlots = { 0: 0, 1: 0 };
  const assignment = new Map<RedisMatchTicket, 0 | 1>();
  for (const party of shuffled) {
    const size = party.players.length;
    if (usedSlots[0] + size <= slotsPerTeam) {
      assignment.set(party, 0);
      usedSlots[0] += size;
    }
    else {
      assignment.set(party, 1);
      usedSlots[1] += size;
    }
  }

  const randomHost = Math.floor(Math.random() * totalPlayers);

  // 4. flatten into per-player entries
  const result: RedisTeamEntry[] = [];
  for (const teamIndex of [
    0,
    1,
  ] as const) {
    let idxInTeam = 0;
    for (const party of shuffled) {
      if (assignment.get(party) !== teamIndex) continue;
      for (const player of party.players) {
        const playerIndex = idxInTeam * 2 + teamIndex;
        const isHost = playerIndex === randomHost;
        result.push({
          playerId: player.id,
          partyId: party.partyId,
          playerIndex,
          teamIndex,
          isHost: isHost,
          ip: (await redisGetPlayer(player.id)).ip,
        });
        idxInTeam++;
      }
    }
  }

  return result;
}

// Create a match from selected tickets
async function createMatch(tickets: RedisMatchTicket[], matchType: string): Promise<void> {
  try {
    // Count total players
    const totalPlayers = tickets.reduce((sum, ticket) => sum + ticket.players.length, 0);
    const matchId = ObjectID().toHexString();
    const resultId = ObjectID().toHexString();

    // Create match object
    const match: RedisMatch = {
      matchId,
      resultId,
      tickets,
      status: "pending",
      createdAt: Date.now(),
      matchType,
      totalPlayers,
      //rollbackPort: randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH),
      rollbackPort: DeployInfo.getRandomRollbackPort(),
    };

    // Get all player IDs from all tickets
    const players = tickets.flatMap((ticket) =>
      ticket.players.map((player) => ({
        playerId: player.id,
        partyId: ticket.partyId,
      })),
    );

    // Store match data
    await redisUpdateMatch(match.matchId, match);

    const notification: MATCH_FOUND_NOTIFICATION = {
      players: await createTeams(tickets),
      matchId,
      matchKey: randomBytes(32).toString("base64"),
      map: await getRandomMapByType(matchType, matchId),
      mode: matchType,
      rollbackPort: match.rollbackPort,
    };

    if (useOnDemandRollback) {
      let deployInfo: IDeployInfo = getDefaultDeployInfo();
      deployInfo.port = match.rollbackPort;
      deployInfo.entrypoint = deployInfo.entrypoint.replace("CHANGEMEDEFAULTPORT", deployInfo.port.toString());
      deployInfo.ovs_server = env.OVS_SERVER;
      const isDeployed = DeployInfo.Deploy(deployInfo);
      if (!isDeployed) {
        logger.error(`${logPrefix} Failed to deploy rollback server for match ${matchId} on port ${deployInfo.port}`);
        // Depending on the desired behavior, you might want to:
        // - Mark the match as failed and notify players
        // - Fall back to an internal rollback server if available
        // For now, we'll just log the error and proceed, but this means the match will likely fail when clients try to connect.
      }
    }

    const playerIds = players.map((p) => p.playerId);
    // Notify about the match creation
    await redisOnGameplayConfigNotified(notification);

    // Pre-create ranked set state at match start (for ranked matches from this worker —
    // custom matches use a separate path). Game 1's matchId IS the setId for the whole
    // set lifetime, so create the set state now rather than lazily at handleMatchEnd.
    //
    // This eliminates the timing race where a player's WS-disconnect handler can't flag
    // ranked_disconnect because player_ranked_set isn't populated yet. Now player_ranked_set
    // is populated from the moment the match is created, so the "disconnected during ranked
    // set" path in websocket.ts works for the entire set duration including game 1 itself
    // and the post-game-1 screen.
    //
    // If this block fails (Redis hiccup, etc.), the system falls back to the legacy
    // pending_winner path. handleMatchEnd / submit_end_of_match_stats both log WARN
    // lines tagged "FALLBACK:" so we can grep for any case where pre-creation failed
    // and we recovered via the older mechanism.
    try {
      const setState = {
        players: notification.players,
        mode: notification.mode,
        gamesPlayed: 0,
        scores: [0, 0],
        checkins: [] as string[],
      };
      await redisClient.set(`ranked_set:${matchId}`, JSON.stringify(setState), { EX: 600 });
      for (const pid of playerIds) {
        await redisClient.set(`player_ranked_set:${pid}`, matchId, { EX: 600 });
      }
      logger.info(`${logPrefix} Pre-created ranked set state for match ${matchId} (${playerIds.length} players, mode=${notification.mode})`);
    } catch (e) {
      logger.error(`${logPrefix} FAILED to pre-create ranked set state for match ${matchId}: ${e} — system will fall back to lazy pending_winner path. Watch for FALLBACK: warns downstream.`);
    }

    // Notify about the matchmaking complete — one per ticket (each has its own matchmakingRequestId)
    for (const ticket of tickets) {
      await redisMatchMakingComplete(
        matchId,
        ticket.matchmakingRequestId,
        ticket.players.map((p) => p.id),
      );
    }
    // NOTE: redisGameServerInstanceReady is NOT called here anymore.
    // It's now triggered by the /ovs_register handler AFTER the rollback server
    // fetches the match config, preventing a race condition where game clients
    // try to connect before the rollback server is ready.

    logger.info(
      `${logPrefix} Created ${matchType} match ${match.resultId} with ${totalPlayers} players across ${tickets.length} tickets`,
    );
  }
  catch (error) {
    logger.error(`${logPrefix} Error creating match: ${error}`);
  }
}

// Worker process
async function checkQueues(): Promise<void> {
  try {
    // First try to make regular 1v1 matches
    const made1v1Match = await process1v1Queue();

    // Then try to make regular 2v2 matches
    const made2v2Match = await process2v2Queue();

    if (made1v1Match) {
      logger.info(`${logPrefix} Successfully created matches in this cycle: 1v1=${made1v1Match}`);
    }

    if (made2v2Match) {
      logger.info(`${logPrefix} Successfully created matches in this cycle: 2v2=${made2v2Match}`);
    }
  }
  catch (error) {
    logger.error(`${logPrefix} Error checking queue: ${error}`);
  }
}
