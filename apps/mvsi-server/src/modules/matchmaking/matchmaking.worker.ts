import { logger } from "@mvsi/logger";
import { ObjectId } from "mongodb";
import { getRandomMapByType } from "../../data/maps";
import {
  GAME_MODES,
  type GameModeConfig,
  type PlayersConfigObject,
  TeamStyle,
} from "../gameModes/gameModes.config";
import { getPlayersConfig } from "../playerConfig/playerConfig.service";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";
import {
  completeMatchmaking,
  getMatchmakingQueue,
  getQueueTickets,
  notifyActiveMatchCreated,
  removeTicketsFromQueue,
} from "./matchmaking.service";
import {
  areTicketsCompatible,
  findMatchedGroups,
  getMatchServerRegion,
  type Region,
} from "./matchmaking.matching";
import {
  MATCH_TYPES,
  MATCHMAKING_MATCH_TICK_CHANNEL,
  type GameplayConfig,
  type MatchmakingTicket,
} from "./matchmaking.types";
import { redisClient } from "@mvsi/redis";
import { assembleArenaMatch } from "../lobby/arena.lobby.service";
import type { ArenaLobby, LobbyPlayer } from "../lobby/lobby.types";

const CHECK_INTERVAL_MS = 2000;

// ─── Arena matchmaking timing config ─────────────────────────────────────────

const ARENA_CONFIG = {
  /** Target: 8 full real-player teams to start immediately. */
  IDEAL_REAL_TEAMS: 8,
  /** Minimum: 4 full real-player teams; bots fill the rest. */
  MIN_REAL_TEAMS: 4,
  /** After this many ms, drop from ideal (8) to minimum (4) requirement. */
  IDEAL_WAIT_MS: 10_000,
  /** After this many ms, start with whatever teams are available. */
  MIN_WAIT_MS: 15_000,
  /** Max teams per arena. */
  TOTAL_TEAMS: 8,
};

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

class MatchCreationError extends Error {
  matchId: string;
  constructor(message: string, matchId: string) {
    super(message);
    this.name = "MatchCreationError";
    this.matchId = matchId;
  }
}

export function startMatchMakingWorker(): void {
  logger.info("Starting matchmaking worker...");
  checkQueues();
  logger.info(`Matchmaking worker started, checking queue every ${CHECK_INTERVAL_MS}ms`);
}

// ─── 1v1 queue ────────────────────────────────────────────────────────────────

async function process1v1Queue(): Promise<boolean> {
  try {
    const queueIds = await getMatchmakingQueue(MATCH_TYPES.ONE_V_ONE);

    if (queueIds.length > 0) {
      await redisClient.publish(MATCHMAKING_MATCH_TICK_CHANNEL, JSON.stringify(queueIds));
    } else {
      return false;
    }

    if (queueIds.length < MATCH_RULES["1v1"].teamsRequired) {
      return false;
    }

    const tickets = await getQueueTickets(queueIds);

    logger.info(`Found ${tickets.length} tickets in 1v1 queue, matching players`);

    const soloTickets = tickets.filter((t) => t.partySize === 1);
    const matchedGroups = findMatchedGroups(soloTickets, MATCH_RULES["1v1"].teamsRequired);

    if (matchedGroups.length === 0) {
      logger.info("No compatible 1v1 matches found this cycle");
      return false;
    }

    let createdAny = false;
    for (const group of matchedGroups) {
      try {
        await removeTicketsFromQueue(MATCH_TYPES.ONE_V_ONE, group);
        await createRetailMatch(group, MATCH_TYPES.ONE_V_ONE);
        createdAny = true;
      } catch (error) {
        logger.error(`Error creating 1v1 match from matched group: ${error}`);
      }
    }

    return createdAny;
  } catch (error) {
    logger.error(`Error processing 1v1 queue: ${error}`);
    return false;
  }
}

// ─── 2v2 queue ────────────────────────────────────────────────────────────────

async function process2v2Queue(): Promise<boolean> {
  try {
    const queueIds = await getMatchmakingQueue(MATCH_TYPES.TWO_V_TWO);

    if (queueIds.length > 0) {
      await redisClient.publish(MATCHMAKING_MATCH_TICK_CHANNEL, JSON.stringify(queueIds));
    } else {
      return false;
    }

    if (queueIds.length < MATCH_RULES["2v2"].totalPlayersRequired) {
      return false;
    }

    const tickets = await getQueueTickets(queueIds);

    logger.info(`Found ${tickets.length} tickets in 2v2 queue, matching players`);

    const matchedGroups = findMatchedGroups(tickets, MATCH_RULES["2v2"].totalPlayersRequired);

    if (matchedGroups.length === 0) {
      logger.info("No compatible 2v2 matches found this cycle");
      return false;
    }

    let createdAny = false;
    for (const group of matchedGroups) {
      try {
        await removeTicketsFromQueue(MATCH_TYPES.TWO_V_TWO, group);
        await createRetailMatch(group, MATCH_TYPES.TWO_V_TWO);
        createdAny = true;
      } catch (error) {
        logger.error(`Error creating 2v2 match from matched group: ${error}`);
      }
    }

    return createdAny;
  } catch (error) {
    logger.error(`Error processing 2v2 queue: ${error}`);
    return false;
  }
}

// ─── Arena queue ──────────────────────────────────────────────────────────────

async function processArenaQueue(): Promise<boolean> {
  try {
    const queueIds = await getMatchmakingQueue(MATCH_TYPES.ARENA);
    if (queueIds.length === 0) return false;

    await redisClient.publish(MATCHMAKING_MATCH_TICK_CHANNEL, JSON.stringify(queueIds));

    const tickets = await getQueueTickets(queueIds);
    const now = Date.now();

    // ── Group tickets by region compatibility ────────────────────────────────
    // Use greedy grouping: anchor on the first ungrouped ticket, pull in
    // every other ticket whose expanded regions overlap with the anchor's.
    const regionGroups = groupTicketsByRegion(tickets, now);

    let createdAny = false;
    for (const compatibleTickets of regionGroups) {
      const matched = await tryAssembleArena(compatibleTickets, now);
      if (matched) createdAny = true;
    }

    return createdAny;
  } catch (error) {
    logger.error(`Error processing arena queue: ${error}`);
    return false;
  }
}

/**
 * Groups arena tickets by region compatibility. Each group contains
 * tickets whose expanded region sets overlap with the anchor ticket.
 */
function groupTicketsByRegion(tickets: MatchmakingTicket[], now: number): MatchmakingTicket[][] {
  const groups: MatchmakingTicket[][] = [];
  const used = new Set<string>();

  for (const anchor of tickets) {
    if (used.has(anchor.matchmakingRequestId)) continue;

    const group: MatchmakingTicket[] = [anchor];
    used.add(anchor.matchmakingRequestId);

    for (const candidate of tickets) {
      if (used.has(candidate.matchmakingRequestId)) continue;
      if (areTicketsCompatible(anchor, candidate, now)) {
        group.push(candidate);
        used.add(candidate.matchmakingRequestId);
      }
    }

    groups.push(group);
  }

  return groups;
}

/**
 * Attempts to assemble an arena match from a set of region-compatible tickets.
 * Returns true if a match was created.
 */
async function tryAssembleArena(tickets: MatchmakingTicket[], now: number): Promise<boolean> {
  // Age of the longest-waiting ticket drives the phase threshold.
  const oldestAgeMs = Math.max(...tickets.map((t) => now - new Date(t.created_at).getTime()));

  // Categorise every team across all tickets.
  type TeamEntry = { players: string[]; ticket: MatchmakingTicket };
  const fullTeamEntries: TeamEntry[] = [];
  const soloEntries: TeamEntry[] = [];

  for (const ticket of tickets) {
    for (const team of ticket.teams) {
      if (team.length >= 2) {
        fullTeamEntries.push({ players: team.slice(0, 2), ticket });
      } else if (team.length === 1) {
        soloEntries.push({ players: [team[0]], ticket });
      }
    }
  }

  // Potential full teams = complete pairs + paired-up solos
  const totalPossibleFullTeams = fullTeamEntries.length + Math.floor(soloEntries.length / 2);

  // Phase 1: require 8 full real-player teams
  if (oldestAgeMs < ARENA_CONFIG.IDEAL_WAIT_MS) {
    if (totalPossibleFullTeams < ARENA_CONFIG.IDEAL_REAL_TEAMS) {
      logger.info(
        `Arena queue: ${totalPossibleFullTeams}/${ARENA_CONFIG.IDEAL_REAL_TEAMS} full teams, waiting (${Math.round(oldestAgeMs / 1000)}s elapsed)`,
      );
      return false;
    }
  }
  // Phase 2: require 4 full real-player teams
  else if (oldestAgeMs < ARENA_CONFIG.MIN_WAIT_MS) {
    if (totalPossibleFullTeams < ARENA_CONFIG.MIN_REAL_TEAMS) {
      logger.info(
        `Arena queue: ${totalPossibleFullTeams}/${ARENA_CONFIG.MIN_REAL_TEAMS} min teams, waiting (${Math.round(oldestAgeMs / 1000)}s elapsed)`,
      );
      return false;
    }
  }
  // Phase 3: proceed with whatever is available

  // ── Select teams for this arena (up to TOTAL_TEAMS = 8) ──────────────────
  const arenaTeams: string[][] = [];
  const usedTicketIds = new Set<string>();

  // First: full 2-player teams (teammates stay together)
  for (const { players, ticket } of fullTeamEntries) {
    if (arenaTeams.length >= ARENA_CONFIG.TOTAL_TEAMS) break;
    arenaTeams.push(players);
    usedTicketIds.add(ticket.matchmakingRequestId);
  }

  // Second: pair up solos from different tickets
  for (
    let i = 0;
    i + 1 < soloEntries.length && arenaTeams.length < ARENA_CONFIG.TOTAL_TEAMS;
    i += 2
  ) {
    arenaTeams.push([soloEntries[i].players[0], soloEntries[i + 1].players[0]]);
    usedTicketIds.add(soloEntries[i].ticket.matchmakingRequestId);
    usedTicketIds.add(soloEntries[i + 1].ticket.matchmakingRequestId);
  }

  // Third: remaining unpaired solo gets their own slot (bot fills the 2nd spot)
  if (soloEntries.length % 2 !== 0 && arenaTeams.length < ARENA_CONFIG.TOTAL_TEAMS) {
    const lastSolo = soloEntries[soloEntries.length - 1];
    arenaTeams.push([lastSolo.players[0]]);
    usedTicketIds.add(lastSolo.ticket.matchmakingRequestId);
  }

  const usedTickets = tickets.filter((t) => usedTicketIds.has(t.matchmakingRequestId));
  if (usedTickets.length === 0) return false;

  // Pick the best server region from used tickets
  const anchorRegion = (usedTickets[0].region ?? "WEST_US") as Region;
  const otherRegions = usedTickets.slice(1).map((t) => (t.region ?? "WEST_US") as Region);
  const chosenRegion = getMatchServerRegion(anchorRegion, ...otherRegions);

  // ── Build a synthetic ArenaLobby from the selected teams ─────────────────
  const makeLobbyPlayer = (pid: string): LobbyPlayer => ({
    Account: { id: pid },
    JoinedAt: new Date(),
    BotSettingSlug: "",
    LobbyPlayerIndex: 0,
    CrossplayPreference: 0,
  });

  const syntheticLobby: ArenaLobby = {
    MatchID: new ObjectId().toHexString(),
    LeaderID: usedTickets[0]?.partyLeaderId ?? "",
    Template: "arena_lobby",
    LobbyType: 0,
    ReadyPlayers: {},
    PlayerGameplayPreferences: {},
    PlayerAutoPartyPreferences: {},
    GameVersion: "",
    HissCrc: 0,
    Platforms: {},
    AllMultiplayParams: {},
    LockedLoadouts: {},
    IsLobbyJoinable: false,
    Teams: arenaTeams.map((players, i) => ({
      TeamIndex: i,
      Players: Object.fromEntries(players.map((pid) => [pid, makeLobbyPlayer(pid)])),
      Length: players.length,
    })),
    players_connection_info: {},
    RematchCount: 0,
  };

  // ── Remove consumed tickets, assemble, then notify completion ─────────────
  await removeTicketsFromQueue(MATCH_TYPES.ARENA, usedTickets);

  const arenaId = await assembleArenaMatch(syntheticLobby, chosenRegion);

  if (arenaId) {
    for (const ticket of usedTickets) {
      await completeMatchmaking(arenaId, ticket.matchmakingRequestId, ticket.playerIds);
    }

    const totalReal = usedTickets.reduce((s, t) => s + t.playerIds.length, 0);
    logger.info(
      `Arena match assembled: arenaId=${arenaId}, region=${chosenRegion}, arenaTeams=${arenaTeams.length}, realPlayers=${totalReal}`,
    );
  }

  return true;
}

// ─── Retail match creation (1v1 / 2v2 / etc.) ────────────────────────────────

async function configurePlayersForMatch(
  tickets: MatchmakingTicket[],
  gameModeConfig: GameModeConfig,
): Promise<PlayersConfigObject> {
  const isFFA = gameModeConfig.TeamStyle === TeamStyle.FFA;

  const numTeams = gameModeConfig.GameModeTeams.length;
  const maxPlayersPerTeam = gameModeConfig.GameModeTeams[0].MaxPlayerPerTeam;

  const allPlayers: { player: string; ticket: MatchmakingTicket }[] = [];
  tickets.forEach((ticket) => {
    ticket.playerIds.forEach((player) => {
      allPlayers.push({ player, ticket });
    });
  });

  const playerConfigs = await getPlayersConfig(allPlayers.map((p) => p.player));

  let playerAssignments: Pick<
    PlayerConfig,
    "AccountId" | "PlayerIndex" | "TeamIndex" | "PartyId" | "PartyMember"
  >[] = [];

  if (isFFA) {
    const shuffledPlayers = [...allPlayers].sort(() => Math.random() - 0.5);

    playerAssignments = shuffledPlayers.map((playerData, index) => ({
      AccountId: playerData.player,
      PartyId: playerData.ticket.partyId,
      PlayerIndex: index,
      TeamIndex: index,
      PartyMember: null,
    }));
  } else {
    const shuffledTickets = [...tickets].sort(() => Math.random() - 0.5);

    type TeamAssignment = {
      teamIndex: number;
      players: { player: string; ticket: MatchmakingTicket }[];
    };

    const teamAssignments: TeamAssignment[] = Array.from({ length: numTeams }, (_, i) => ({
      teamIndex: i,
      players: [],
    }));

    shuffledTickets.forEach((ticket) => {
      const availableTeams = teamAssignments.filter(
        (team) => team.players.length + ticket.partySize <= maxPlayersPerTeam,
      );

      const selectedTeam = availableTeams[Math.floor(Math.random() * availableTeams.length)];

      ticket.playerIds.forEach((player) => {
        selectedTeam.players.push({ player, ticket });
      });
    });

    teamAssignments.forEach((team) => {
      team.players.forEach((playerData, playerIdx) => {
        const teammate =
          playerData.ticket.partySize > 1
            ? playerData.ticket.playerIds.find((p) => p !== playerData.player)
            : null;

        playerAssignments.push({
          AccountId: playerData.player,
          PlayerIndex: team.teamIndex + playerIdx * numTeams,
          TeamIndex: team.teamIndex,
          PartyId: playerData.ticket.partyId,
          PartyMember: teammate ?? null,
        });
      });
    });
  }

  const players: PlayersConfigObject = {};
  playerAssignments.forEach((assignment) => {
    const playerConfig = playerConfigs.get(assignment.AccountId);
    if (!playerConfig) {
      throw new MatchCreationError("Could not find PlayerConfig", "");
    }
    players[assignment.AccountId] = {
      ...assignment,
      ...playerConfig,
    };
  });

  return players;
}

async function createRetailMatch(
  tickets: MatchmakingTicket[],
  matchType: MATCH_TYPES,
): Promise<void> {
  try {
    const totalPlayers = tickets.reduce((sum, ticket) => sum + ticket.playerIds.length, 0);

    const gameMode = GAME_MODES.get(matchType);
    if (!gameMode) {
      throw new Error("Failed to get gamed mode");
    }

    // Pick the best server region from all matched tickets
    const anchorRegion = (tickets[0].region ?? "WEST_US") as Region;
    const otherRegions = tickets.slice(1).map((t) => (t.region ?? "WEST_US") as Region);
    const chosenRegion = getMatchServerRegion(anchorRegion, ...otherRegions);

    const gameplayConfig: GameplayConfig = {
      GameplayConfig: {
        Spectators: {},
        TeamData: [],
        bIsTutorial: false,
        bIsOnlineMatch: true,
        bIsRift: false,
        bIsPvP: true,
        bIsCustomGame: false,
        bIsRanked: false,
        bIsCasualSpecial: false,
        Cluster: chosenRegion,
        bAllowMapHazards: true,
        WorldBuffs: [],
        RiftNodeAttunement: "Attunements:None",
        CountdownDisplay: "CountdownTypes:XvY",
        HudSettings: {
          bDisplayPortraits: true,
          bDisplayTimer: true,
          bDisplayStocks: true,
        },
        MatchDurationSeconds: gameMode.MatchDuration,
        ScoreEvaluationRule: "TargetScoreIsWin",
        ScoreAttributionRule: "AttributeToAttacker",
        CustomGameSettings: {
          NumRingouts: gameMode.NumRingouts,
          MatchTime: gameMode.MatchDuration,
          bHazardsEnabled: gameMode.bHazardsEnabled,
          bShieldsEnabled: gameMode.bShieldsEnabled,
        },
        ArenaModeInfo: null,
        RiftNodeId: "",
        bModeGrantsProgress: true,
        EventQueueSlug: "",
        MatchId: new ObjectId().toHexString(),
        Created: new Date(),
        Map: getRandomMapByType(matchType),
        ModeString: matchType,
        Players: await configurePlayersForMatch(tickets, gameMode),
      },
    };

    const matchId = await notifyActiveMatchCreated("", gameplayConfig);

    for (const ticket of tickets) {
      await completeMatchmaking(
        matchId,
        ticket.matchmakingRequestId,
        ticket.playerIds.map((p) => p),
      );
    }

    logger.info(
      `Created ${matchType} match ${matchId} with ${totalPlayers} players across ${tickets.length} tickets`,
    );
  } catch (error) {
    logger.error(`Error creating match: ${error}`);
  }
}

// ─── Worker loop ──────────────────────────────────────────────────────────────

async function checkQueues(): Promise<void> {
  try {
    const made1v1Match = await process1v1Queue();
    const made2v2Match = await process2v2Queue();
    const madeArenaMatch = await processArenaQueue();

    if (made1v1Match) logger.info("Successfully created 1v1 match this cycle");
    if (made2v2Match) logger.info("Successfully created 2v2 match this cycle");
    if (madeArenaMatch) logger.info("Successfully created arena match this cycle");
  } catch (error) {
    logger.error(`Error checking queue: ${error}`);
  }
  setTimeout(checkQueues, CHECK_INTERVAL_MS);
}
