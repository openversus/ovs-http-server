import { logger } from "../config/logger";
import { redisClient } from "../config/redis";
import { PlayerStatsModel, RecentMatchEntry, RecentMatchPlayerStats } from "../database/PlayerStats";
import { MatchArchiveModel } from "../database/MatchArchive";
import * as zstd from "zstd-napi";

const logPrefix = "[Services.Stats]:";

// ── Tossup threshold ──
// ~84 ELO difference → expected ≈ 0.56 / 0.44. Anything within this range is
// a coin flip (same ranked tier since ranks are ~100 ELO apart).
const TOSSUP_LOW = 0.44;
const TOSSUP_HIGH = 0.56;

/**
 * Extracts the stat name from a character-specific mission/ability key.
 * The game's slug → prefix mapping is inconsistent (BananaGuard, C025, Harley,
 * WonderWoman, Stripe uses Hitbox:, Marceline has no category prefix, etc.).
 * Since each player's PMU only has NON-ZERO values for their actual character,
 * we match by prefix PATTERN and attribute any non-zero Fighter:/Hitbox:/Marceline:
 * stat to the player's current character.
 */
function extractFighterStatName(key: string): string | null {
  if (key.startsWith("Fighter:") || key.startsWith("Hitbox:")) {
    const parts = key.split(":");
    if (parts.length >= 3) return parts.slice(2).join(":");
  }
  if (key.startsWith("Marceline:")) {
    return key.slice("Marceline:".length);
  }
  return null;
}

/**
 * Converts "Stat:Game:Character:TotalAttackDamageDealt" → "totalAttackDamageDealt".
 * Handles "Stat:Game:Character:Stock:DamageTaken" → "stockDamageTaken".
 */
function normalizeAggregateKey(gameKey: string): string {
  const prefix = "Stat:Game:Character:";
  if (!gameKey.startsWith(prefix)) return "";
  const rest = gameKey.slice(prefix.length).replace(/:/g, "");
  return rest.charAt(0).toLowerCase() + rest.slice(1);
}

// ════════════════════════════════════════════════════════════════════════
// recordSetStats — called fire-and-forget from processSetResult (per-SET)
// Handles: set-level wins/losses, matchup outcomes, teammate outcomes
// Does NOT write recent matches or archives — those happen per-GAME in recordGameStats.
// ════════════════════════════════════════════════════════════════════════

export interface RecordSetStatsParams {
  matchId: string;
  mode: string;
  setScore: [number, number];
  winnerTeam: number;
  isConcede: boolean;
  isPregameDodge?: boolean; // only true for pregame dodges (no game played)
  winnerIds: string[];
  loserIds: string[];
  winnerRatings: any[];
  loserRatings: any[];
  playerCharacters: Map<string, string>;
  deltas: Map<string, number>;
  avgWinnerElo: number;
  avgLoserElo: number;
  expectedScores: Map<string, number>;
}

export async function recordSetStats(params: RecordSetStatsParams): Promise<void> {
  const {
    matchId, mode, setScore, winnerTeam, isConcede, isPregameDodge,
    winnerIds, loserIds, winnerRatings, loserRatings,
    playerCharacters, deltas, avgWinnerElo, avgLoserElo, expectedScores,
  } = params;

  const is1v1 = mode === "1v1" || mode.includes("1v1");
  const charsField = is1v1 ? "characters_1v1" : "characters_2v2";
  const allIds = [...winnerIds, ...loserIds];

  const updates: Promise<any>[] = [];

  for (const playerId of allIds) {
    const isWinner = winnerIds.includes(playerId);
    const myChar = playerCharacters.get(playerId) || "unknown";
    const expected = expectedScores.get(playerId) ?? 0.5;

    const opponentIds = isWinner ? loserIds : winnerIds;
    const teammateIds = (isWinner ? winnerIds : loserIds).filter((id) => id !== playerId);
    const opponentChar = playerCharacters.get(opponentIds[0]) || "unknown";
    const teammateChar = teammateIds.length > 0 ? (playerCharacters.get(teammateIds[0]) || "unknown") : undefined;

    const isTossup = expected >= TOSSUP_LOW && expected <= TOSSUP_HIGH;
    const isUpset = isWinner && expected < TOSSUP_LOW;
    const isChoke = !isWinner && expected > TOSSUP_HIGH;

    const inc: Record<string, number> = {};
    const charPath = `${charsField}.${myChar}`;

    // Character-level wins/losses (set-level)
    inc[`${charPath}.${isWinner ? "wins" : "losses"}`] = 1;
    // Character-level dodge tracking — ONLY pregame dodges (no game was played).
    // Mid-match alt-F4 or between-games leaves count as real set wins/losses.
    //   actualPlayedWins = wins - dodgeWins
    //   actualPlayedLosses = losses - dodgeLosses
    if (isPregameDodge) {
      inc[`${charPath}.${isWinner ? "dodgeWins" : "dodgeLosses"}`] = 1;
    }
    // Character-level upset/choke/tossup tracking (aggregated across all opponents)
    if (isUpset) inc[`${charPath}.upsets`] = 1;
    if (isChoke) inc[`${charPath}.chokes`] = 1;
    if (isTossup) inc[`${charPath}.${isWinner ? "tossupWins" : "tossupLosses"}`] = 1;

    // Matchup-level outcomes vs opponent character (same stats, per-opponent)
    const matchupPath = `${charPath}.matchups.${opponentChar}`;
    inc[`${matchupPath}.${isWinner ? "wins" : "losses"}`] = 1;
    if (isUpset) inc[`${matchupPath}.upsets`] = 1;
    if (isChoke) inc[`${matchupPath}.chokes`] = 1;
    if (isTossup) inc[`${matchupPath}.${isWinner ? "tossupWins" : "tossupLosses"}`] = 1;
    // Matchup-level "dodges" also only counts pregame dodges (consistent with character-level)
    if (isPregameDodge && isWinner) inc[`${matchupPath}.dodges`] = 1;

    // Teammate-level outcomes (2v2 only)
    if (!is1v1 && teammateChar) {
      const teammatePath = `${charPath}.teammates.${teammateChar}`;
      inc[`${teammatePath}.${isWinner ? "wins" : "losses"}`] = 1;
      if (isUpset) inc[`${teammatePath}.upsets`] = 1;
      if (isChoke) inc[`${teammatePath}.chokes`] = 1;
    }

    // Recent match history is now written per-GAME in recordGameStats, not here.
    // This function only handles set-level win/loss badges and matchup counters.

    updates.push(
      PlayerStatsModel.updateOne(
        { account_id: playerId },
        {
          $inc: inc,
          $set: { updated_at: Date.now() },
        },
        { upsert: true },
      ).catch((err) => logger.error(`${logPrefix} Failed to update set stats for ${playerId}: ${err}`)),
    );
  }

  await Promise.all(updates);
  logger.info(`${logPrefix} Recorded set stats for ${allIds.length} players, match ${matchId}`);
}

// ════════════════════════════════════════════════════════════════════════
// recordGameStats — called fire-and-forget from submit_end_of_match_stats (per-GAME)
// Handles:
//   - Per-character badges ($inc ringouts, totalDamageDealt; $max highestDamageDealt)
//   - Per-character fighterStats ($inc Fighter:/Hitbox:/Marceline: fields)
//   - Aggregate combat stats ($inc all Stat:Game:Character:* fields, mode-agnostic)
//   - Full match archive (zstd-compressed PMU + metadata, upserted per match_id)
// ════════════════════════════════════════════════════════════════════════

export async function recordGameStats(
  matchId: string,
  endOfMatchStats: any,
  submittingPlayerId?: string,
): Promise<void> {
  if (!matchId || !endOfMatchStats) return;

  const pmu = endOfMatchStats.PlayerMissionUpdates as Record<string, Record<string, any>> | undefined;
  if (!pmu || typeof pmu !== "object") return;

  // Determine mode from matchConfig
  const configRaw = await redisClient.get(matchId);
  const mode = configRaw ? JSON.parse(configRaw).mode : "1v1";
  const is1v1 = mode === "1v1" || mode.includes("1v1");
  const charsField = is1v1 ? "characters_1v1" : "characters_2v2";

  // Get character mapping for all players in this match
  const matchCharsRaw = await redisClient.get(`match_characters:${matchId}`);
  const matchChars = matchCharsRaw ? JSON.parse(matchCharsRaw) : {};

  // ── Per-player stats updates ──
  const updates: Promise<any>[] = [];

  for (const [accountId, playerPmu] of Object.entries(pmu)) {
    if (!playerPmu || typeof playerPmu !== "object") continue;

    // Resolve player's character for this match
    let myChar = matchChars[accountId];
    if (!myChar) {
      const conn = await redisClient.hGetAll(`connections:${accountId}`) as any;
      myChar = conn?.character;
    }
    if (!myChar) continue;

    const charPath = `${charsField}.${myChar}`;
    const inc: Record<string, number> = {};
    const max: Record<string, number> = {};

    // Extract per-character badge values from THIS game
    const thisGameRingouts = Math.round(Number(playerPmu["Stat:Game:Character:TotalRingouts"]) || 0);
    // Use TotalDamageDealt (matches recent_matches.damage). Falls back to
    // TotalAttackDamageDealt for legacy PMU payloads that didn't ship Total.
    const thisGameDamage = Math.round(
      Number(playerPmu["Stat:Game:Character:TotalDamageDealt"]) ||
      Number(playerPmu["Stat:Game:Character:TotalAttackDamageDealt"]) || 0,
    );

    // Badges: $inc accumulated, $max for highest-ever
    if (thisGameRingouts > 0) inc[`${charPath}.ringouts`] = thisGameRingouts;
    if (thisGameDamage > 0) {
      inc[`${charPath}.totalDamageDealt`] = thisGameDamage;
      max[`${charPath}.highestDamageDealt`] = thisGameDamage;
    }

    // Aggregate: $inc every Stat:Game:Character:* field (mode-agnostic)
    for (const [key, val] of Object.entries(playerPmu)) {
      if (!key.startsWith("Stat:Game:Character:")) continue;
      if (typeof val !== "number" || val <= 0) continue;
      const field = normalizeAggregateKey(key);
      if (field) inc[`aggregate.${field}`] = Math.round(val);
    }

    // Character-specific fighter stats (Fighter:/Hitbox:/Marceline: with val > 0)
    for (const [key, val] of Object.entries(playerPmu)) {
      if (typeof val !== "number" || val <= 0) continue;
      const statName = extractFighterStatName(key);
      if (statName) inc[`${charPath}.fighterStats.${statName}`] = Math.round(val);
    }

    // ── Recent match entry (per-GAME) ──
    const recentField = is1v1 ? "recent_matches_1v1" : "recent_matches_2v2";
    const winningTeamIdx = endOfMatchStats.WinningTeamIndex ?? -1;
    const rawScore = endOfMatchStats.Score;
    const gameScore: [number, number] = Array.isArray(rawScore)
      ? [rawScore[0] ?? 0, rawScore[1] ?? 0]
      : [0, 0];

    // Determine team indexes from match config (MATCH_FOUND_NOTIFICATION format)
    const matchConfig = configRaw ? JSON.parse(configRaw) : null;
    const teamMap = new Map<string, number>(); // playerId → teamIndex
    if (matchConfig?.players && Array.isArray(matchConfig.players)) {
      for (const p of matchConfig.players) {
        if (p.playerId && p.teamIndex !== undefined) {
          teamMap.set(p.playerId, p.teamIndex);
        }
      }
    }

    const myTeamIndex = teamMap.get(accountId) ?? -1;
    const didWin = winningTeamIdx >= 0 && myTeamIndex === winningTeamIdx;

    // Build per-player combat stats from PMU for match breakdown
    const allPlayerStats: RecentMatchPlayerStats[] = [];
    for (const [pid, pPmu] of Object.entries(pmu)) {
      if (!pPmu || typeof pPmu !== "object") continue;
      const pChar = matchChars[pid] || "unknown";
      const pTeam = teamMap.get(pid) ?? -1;
      const pWon = winningTeamIdx >= 0 && pTeam === winningTeamIdx;
      allPlayerStats.push({
        accountId: pid,
        character: pChar,
        teamIndex: pTeam,
        damage: Math.round(Number(pPmu["Stat:Game:Character:TotalDamageDealt"]) ||
                           Number(pPmu["Stat:Game:Character:TotalAttackDamageDealt"]) || 0),
        ringouts: Math.round(Number(pPmu["Stat:Game:Character:TotalRingouts"]) || 0),
        deaths: 0, // derived below from opponent ringouts
        isWinner: pWon,
      });
    }
    // Derive deaths: each player's deaths = sum of opponent team's ringouts
    for (const ps of allPlayerStats) {
      ps.deaths = allPlayerStats
        .filter(other => other.teamIndex !== ps.teamIndex)
        .reduce((sum, other) => sum + other.ringouts, 0);
    }

    const recentEntry: RecentMatchEntry = {
      matchId,
      timestamp: Date.now(),
      mode,
      map: matchConfig?.map || "",
      result: didWin ? "win" : "loss",
      score: gameScore,
      players: allPlayerStats,
    };

    const updateDoc: any = {
      $set: { updated_at: Date.now() },
      $push: { [recentField]: { $each: [recentEntry], $slice: -10 } },
    };
    if (Object.keys(inc).length > 0) updateDoc.$inc = inc;
    if (Object.keys(max).length > 0) updateDoc.$max = max;

    updates.push(
      PlayerStatsModel.updateOne(
        { account_id: accountId },
        updateDoc,
        { upsert: true },
      ).catch((err) => logger.error(`${logPrefix} Failed to update game stats for ${accountId}: ${err}`)),
    );
  }

  // ── Archive: one compressed doc per GAME (upserted by match_id to dedup) ──
  const archivePayload = {
    match_id: matchId,
    mode,
    timestamp: Date.now(),
    winning_team: endOfMatchStats.WinningTeamIndex,
    score: endOfMatchStats.Score,
    players: Object.keys(pmu).map((id) => ({
      account_id: id,
      character: matchChars[id] || "unknown",
    })),
    network_stats: endOfMatchStats.PlayerNetworkStats,
    mission_updates: pmu,
  };

  const jsonBuf = Buffer.from(JSON.stringify(archivePayload));
  const compressed = zstd.compress(jsonBuf, { compressionLevel: 9 });

  updates.push(
    MatchArchiveModel.updateOne(
      { match_id: matchId },
      {
        $setOnInsert: {
          match_id: matchId,
          timestamp: new Date(),
          compressed_data: compressed,
        },
      },
      { upsert: true },
    ).catch((err) => logger.error(`${logPrefix} Failed to archive ${matchId}: ${err}`)),
  );

  await Promise.all(updates);
  logger.info(`${logPrefix} Recorded game stats for ${Object.keys(pmu).length} player(s), match ${matchId} (${compressed.length} bytes archived)`);
}
