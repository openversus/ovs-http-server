import { EloRatingModel } from "../database/EloRating";
import { redisGetMatchConfig, redisGetMatch, redisClient, RedisPlayerConnection } from "../config/redis";
import { logger } from "../config/logger";
import { PlayerTesterModel } from "../database/PlayerTester";
import { Types } from "mongoose";
import env from "../env/env";
import { recordSetStats } from "./statsService";

const serviceName = "Services.Elo";
const logPrefix = `[${serviceName}]:`;

const DEFAULT_ELO = env.DEFAULT_ELO || 0;

// --- ELO Configuration ---
const PROVISIONAL_SET_THRESHOLD = 20; // First N sets use higher K
const K_PROVISIONAL = 64;
const K_ESTABLISHED = 32;
const ELO_DIVISOR = 800;

// Set score modifiers
const SET_SCORE_2_0_MODIFIER = 1.0;   // Dominant win
const SET_SCORE_2_1_MODIFIER = 0.85;  // Close set

// Floor/cap
const MIN_GAIN = 6;
const MAX_LOSS = -24;
const MIN_LOSS = -3;

// Win streak bonuses
const STREAK_3_BONUS = 0.20;  // +20%
const STREAK_5_BONUS = 0.35;  // +35%
const STREAK_7_BONUS = 0.50;  // +50%

/**
 * Get K-factor based on total sets played.
 */
function getKFactor(totalSets: number): number {
  return totalSets < PROVISIONAL_SET_THRESHOLD ? K_PROVISIONAL : K_ESTABLISHED;
}

/**
 * Get win streak bonus multiplier.
 */
function getStreakBonus(streak: number): number {
  if (streak >= 7) return STREAK_7_BONUS;
  if (streak >= 5) return STREAK_5_BONUS;
  if (streak >= 3) return STREAK_3_BONUS;
  return 0;
}

/**
 * Get set score modifier (2-0 vs 2-1).
 */
function getSetScoreModifier(setScore: [number, number], winnerTeam: number): number {
  const loserWins = winnerTeam === 0 ? setScore[1] : setScore[0];
  return loserWins === 0 ? SET_SCORE_2_0_MODIFIER : SET_SCORE_2_1_MODIFIER;
}

/**
 * Calculate expected score for a player against an opponent rating.
 * Returns value between 0 and 1.
 */
function expectedScore(playerElo: number, opponentElo: number): number {
  return 1 / (1 + Math.pow(10, (opponentElo - playerElo) / ELO_DIVISOR));
}

/**
 * Get or create a player's ELO rating document.
 * Also updates the username if it has changed.
 */
export async function getOrCreateRating(accountId: string, username: string = "") {
  let rating = await EloRatingModel.findOne({ account_id: accountId });
  if (!rating) {
    rating = await EloRatingModel.create({
      account_id: accountId,
      username,
      elo_1v1: DEFAULT_ELO,
      elo_2v2: DEFAULT_ELO,
      wins_1v1: 0,
      losses_1v1: 0,
      wins_2v2: 0,
      losses_2v2: 0,
      updated_at: Date.now(),
    });
    logger.info(`${logPrefix} Created new ELO rating for player ${accountId} (${username})`);
  } else if (username && username !== rating.username) {
    // Update username if it has changed
    rating.username = username;
    await EloRatingModel.updateOne(
      { account_id: accountId },
      { $set: { username } },
    );
  }
  return rating;
}

/**
 * Look up a player's username from the Redis connection data.
 */
async function getPlayerUsername(playerId: string): Promise<string> {
  try {
    const connection = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;
    return connection?.username || "";
  } catch {
    return "";
  }
}

/**
 * Process a SET result (best-of-3) and update ELO ratings.
 * Called when a ranked set ends (2-0, 2-1, or concede).
 *
 * @param winnerIds - Player IDs on the winning team
 * @param loserIds - Player IDs on the losing team
 * @param mode - "1v1" or "2v2"
 * @param setScore - [team0Wins, team1Wins]
 * @param winnerTeam - Which team won (0 or 1)
 * @param isConcede - Whether the set ended by concede
 */
export async function processSetResult(
  winnerIds: string[],
  loserIds: string[],
  mode: string,
  setScore: [number, number],
  winnerTeam: number,
  isConcede: boolean = false,
  playerCharacters: Map<string, string> = new Map(), // playerId → character_slug
  matchId?: string, // optional — used to check for server crash flag
  isPregameDodge: boolean = false, // true only if dodge happened before match_started (no game played)
): Promise<{ deltas: Map<string, number>; rankUpdates: Map<string, any> }> {
  const deltas = new Map<string, number>();
  const rankUpdates = new Map<string, any>(); // playerId → MvsRankedServerMatchPayload

  // If the rollback server crashed mid-match, skip ELO — not fair to process
  if (matchId) {
    const crashed = await redisClient.get(`match_server_crash:${matchId}`);
    if (crashed) {
      logger.info(`[Services.Elo]: Skipping ELO for match ${matchId} — rollback server crashed (match_server_crash flag set)`);
      return { deltas, rankUpdates };
    }
  }
  const expectedScores = new Map<string, number>();
  const is1v1 = mode === "1v1" || mode.includes("1v1");
  const eloField = is1v1 ? "elo_1v1" : "elo_2v2";
  const winsField = is1v1 ? "wins_1v1" : "wins_2v2";
  const lossesField = is1v1 ? "losses_1v1" : "losses_2v2";
  const streakField = is1v1 ? "win_streak_1v1" : "win_streak_2v2";
  const charsField = is1v1 ? "characters_1v1" : "characters_2v2";

  const setModifier = isConcede ? SET_SCORE_2_0_MODIFIER : getSetScoreModifier(setScore, winnerTeam);

  // Get all ratings
  const winnerRatings = await Promise.all(
    winnerIds.map(async (id) => {
      const username = await getPlayerUsername(id);
      return getOrCreateRating(id, username);
    }),
  );
  const loserRatings = await Promise.all(
    loserIds.map(async (id) => {
      const username = await getPlayerUsername(id);
      return getOrCreateRating(id, username);
    }),
  );

  // Use per-character ELO for matching calculation
  const getCharElo = (rating: any, playerId: string): number => {
    const charSlug = playerCharacters.get(playerId);
    if (charSlug) {
      // Use character-specific ELO, defaulting to 0 for new characters
      return rating[charsField]?.[charSlug]?.elo ?? DEFAULT_ELO;
    }
    return rating[eloField]; // Fallback to global only if no character info
  };

  const avgWinnerElo = winnerRatings.reduce((sum, r) => sum + getCharElo(r, r.account_id), 0) / winnerRatings.length;
  const avgLoserElo = loserRatings.reduce((sum, r) => sum + getCharElo(r, r.account_id), 0) / loserRatings.length;

  // Update winners
  for (const rating of winnerRatings) {
    const charSlug = playerCharacters.get(rating.account_id) || "";
    const charData = rating[charsField]?.[charSlug] || { elo: DEFAULT_ELO, wins: 0, losses: 0, streak: 0 };
    const playerElo = charSlug ? charData.elo : rating[eloField];
    const totalSets = rating[winsField] + rating[lossesField];
    const K = getKFactor(totalSets);
    const expected = expectedScore(playerElo, avgLoserElo);
    expectedScores.set(rating.account_id, expected);
    const currentStreak = (charSlug ? charData.streak : (rating[streakField] || 0)) + 1;
    const streakBonus = getStreakBonus(currentStreak);

    let delta = Math.round(K * (1 - expected) * setModifier * (1 + streakBonus));
    delta = Math.max(MIN_GAIN, delta);
    const newElo = Math.max(0, playerElo + delta);
    deltas.set(rating.account_id, delta);

    // Build update
    const update: any = {
      $set: { [streakField]: currentStreak, updated_at: Date.now() },
      $inc: { [winsField]: 1 },
    };

    // Update per-character ELO
    if (charSlug) {
      update.$set[`${charsField}.${charSlug}`] = {
        elo: newElo,
        wins: charData.wins + 1,
        losses: charData.losses,
        streak: currentStreak,
      };
      // Update global ELO to best character's ELO
      const allChars = { ...(rating[charsField] || {}), [charSlug]: { elo: newElo } };
      const bestElo = Math.max(...Object.values(allChars).map((c: any) => c.elo));
      update.$set[eloField] = bestElo;
    } else {
      update.$set[eloField] = newElo;
    }

    await EloRatingModel.updateOne({ account_id: rating.account_id }, update);

    // Build MvsRankedServerMatchPayload for FullRankUpdate notification
    rankUpdates.set(rating.account_id, {
      Season: "Season:SeasonFive",
      Mode: is1v1 ? "1v1" : "2v2",
      Character: charSlug || "",
      TotalGamesPlayedForMode: (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      TotalSetsPlayedForMode: (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      StarDelta: 0,
      StarSources: {},
      PreviousGoldenStar: 0,
      NewGoldenStar: 0,
      RpDelta: delta,
      UpdatedCharacterRecord: {
        CurrentPoints: newElo,
        MaxPoints: newElo,
        GamesPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        SetsPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        Wins: charSlug ? charData.wins + 1 : (rating[winsField] || 0) + 1,
        Losses: charSlug ? charData.losses : rating[lossesField] || 0,
        DamageDealt: 0,
        DamageTaken: 0,
        Ringouts: 0,
        Deaths: 0,
      },
      UpdatedBestCharacterRecord: {
        CharacterSlug: charSlug || "",
        CurrentPoints: newElo,
        MaxPoints: newElo,
        GamesPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        SetsPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      },
    });

    logger.info(
      `${logPrefix} ${rating.account_id} (${rating.username}) SET WIN [${charSlug || "global"}]: ${playerElo} → ${newElo} (+${delta}) ` +
      `[K=${K}, expected=${expected.toFixed(3)}, set=${setScore}, streak=${currentStreak}, modifier=${setModifier}]`,
    );
  }

  // Update losers
  for (const rating of loserRatings) {
    const charSlug = playerCharacters.get(rating.account_id) || "";
    const charData = rating[charsField]?.[charSlug] || { elo: DEFAULT_ELO, wins: 0, losses: 0, streak: 0 };
    const playerElo = charSlug ? charData.elo : rating[eloField];
    const totalSets = rating[winsField] + rating[lossesField];
    const K = getKFactor(totalSets);
    const expected = expectedScore(playerElo, avgWinnerElo);
    expectedScores.set(rating.account_id, expected);

    let delta = Math.round(K * (0 - expected) * setModifier);
    delta = Math.max(MAX_LOSS, Math.min(MIN_LOSS, delta));
    const newElo = Math.max(0, playerElo + delta);
    deltas.set(rating.account_id, delta);

    const update: any = {
      $set: { [streakField]: 0, updated_at: Date.now() },
      $inc: { [lossesField]: 1 },
    };

    if (charSlug) {
      update.$set[`${charsField}.${charSlug}`] = {
        elo: newElo,
        wins: charData.wins,
        losses: charData.losses + 1,
        streak: 0,
      };
      const allChars = { ...(rating[charsField] || {}), [charSlug]: { elo: newElo } };
      const bestElo = Math.max(...Object.values(allChars).map((c: any) => c.elo));
      update.$set[eloField] = bestElo;
    } else {
      update.$set[eloField] = newElo;
    }

    await EloRatingModel.updateOne({ account_id: rating.account_id }, update);

    rankUpdates.set(rating.account_id, {
      Season: "Season:SeasonFive",
      Mode: is1v1 ? "1v1" : "2v2",
      Character: charSlug || "",
      TotalGamesPlayedForMode: (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      TotalSetsPlayedForMode: (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      StarDelta: 0,
      StarSources: {},
      PreviousGoldenStar: 0,
      NewGoldenStar: 0,
      RpDelta: delta,
      UpdatedCharacterRecord: {
        CurrentPoints: newElo,
        MaxPoints: newElo,
        GamesPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        SetsPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        Wins: charSlug ? charData.wins : rating[winsField] || 0,
        Losses: charSlug ? charData.losses + 1 : (rating[lossesField] || 0) + 1,
        DamageDealt: 0,
        DamageTaken: 0,
        Ringouts: 0,
        Deaths: 0,
      },
      UpdatedBestCharacterRecord: {
        CharacterSlug: charSlug || "",
        CurrentPoints: newElo,
        MaxPoints: newElo,
        GamesPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
        SetsPlayed: charSlug ? charData.wins + charData.losses + 1 : (rating[winsField] || 0) + (rating[lossesField] || 0) + 1,
      },
    });

    logger.info(
      `${logPrefix} ${rating.account_id} (${rating.username}) SET LOSS [${charSlug || "global"}]: ${playerElo} → ${newElo} (${delta}) ` +
      `[K=${K}, expected=${expected.toFixed(3)}, set=${setScore}, modifier=${setModifier}]`,
    );
  }

  logger.info(
    `${logPrefix} Processed set result: winners=[${winnerIds}] losers=[${loserIds}] ` +
    `score=${setScore} concede=${isConcede} (avg ELO: winners=${Math.round(avgWinnerElo)} vs losers=${Math.round(avgLoserElo)})`,
  );

  // Fire-and-forget: record stats for all three tiers
  recordSetStats({
    matchId: matchId || `set_${Date.now()}`,
    mode, setScore, winnerTeam, isConcede, isPregameDodge,
    winnerIds, loserIds, winnerRatings, loserRatings,
    playerCharacters, deltas, avgWinnerElo, avgLoserElo, expectedScores,
  }).catch((err) => logger.error(`${logPrefix} Stats recording failed: ${err}`));

  return { deltas, rankUpdates };
}

/**
 * Process a match result and update ELO ratings.
 * Called from /ovs_end_match when the rollback server reports a winner.
 *
 * For 1v1: standard ELO — each player's expected score vs opponent.
 * For 2v2: per-player ELO — each player's expected score vs opposing TEAM average.
 *   This prevents boosting: a high-rated player paired with a low-rated friend
 *   gains less because their individual expected score is already high.
 *
 * @param matchId - The match ID
 * @param winningTeamIndex - The team index that won (0 or 1)
 */
export async function processMatchResult(matchId: string, winningTeamIndex: number): Promise<void> {
  try {
    // Get match config from Redis (contains player team assignments)
    const matchConfig = await redisGetMatchConfig(matchId);
    if (!matchConfig || !matchConfig.players) {
      logger.error(`${logPrefix} Cannot process match result: match config not found for ${matchId}`);
      return;
    }

    // Check if this is a password match — skip ELO updates
    const matchData = await redisGetMatch(matchId);
    if (matchData && matchData.isPasswordMatch) {
      logger.info(`${logPrefix} Skipping ELO update for password match ${matchId}`);
      return;
    }

    // Determine match type from mode field
    const mode = matchConfig.mode;
    const is1v1 = mode === "1v1" || mode.startsWith("1v1");
    const is2v2 = mode === "2v2" || mode.startsWith("2v2");

    if (!is1v1 && !is2v2) {
      logger.info(`${logPrefix} Skipping ELO update for non-ranked mode: ${mode}`);
      return;
    }

    // Split players into winners and losers based on teamIndex
    const winners = matchConfig.players.filter((p) => p.teamIndex === winningTeamIndex);
    const losers = matchConfig.players.filter((p) => p.teamIndex !== winningTeamIndex);

    if (winners.length === 0 || losers.length === 0) {
      logger.error(`${logPrefix} Invalid team split for match ${matchId}: ${winners.length} winners, ${losers.length} losers`);
      return;
    }

    const eloField = is1v1 ? "elo_1v1" : "elo_2v2";
    const winsField = is1v1 ? "wins_1v1" : "wins_2v2";
    const lossesField = is1v1 ? "losses_1v1" : "losses_2v2";

    // Get all player ratings (and update usernames from Redis)
    const winnerRatings = await Promise.all(
      winners.map(async (p) => {
        const username = await getPlayerUsername(p.playerId);
        return getOrCreateRating(p.playerId, username);
      }),
    );
    const loserRatings = await Promise.all(
      losers.map(async (p) => {
        const username = await getPlayerUsername(p.playerId);
        return getOrCreateRating(p.playerId, username);
      }),
    );

    // Calculate opposing team averages
    const avgWinnerElo = winnerRatings.reduce((sum, r) => sum + r[eloField], 0) / winnerRatings.length;
    const avgLoserElo = loserRatings.reduce((sum, r) => sum + r[eloField], 0) / loserRatings.length;

    // --- Per-player ELO updates ---
    // Each player's expected score is computed against the opposing TEAM average.
    // This means a high-rated player on the winning team gains LESS (their expected
    // score vs a weak team is already high) while a low-rated player gains MORE.
    // This prevents boosting and provides fair individual calibration.

    // Update each winner individually
    for (const rating of winnerRatings) {
      const playerElo = rating[eloField];
      const totalGames = rating[winsField] + rating[lossesField];
      const K = getKFactor(totalGames);
      const expected = expectedScore(playerElo, avgLoserElo);
      const delta = Math.round(K * (1 - expected));
      const newElo = Math.max(0, playerElo + delta);

      await EloRatingModel.updateOne(
        { account_id: rating.account_id },
        {
          $set: { [eloField]: newElo, updated_at: Date.now() },
          $inc: { [winsField]: 1 },
        },
      );
      logger.info(
        `${logPrefix} ${rating.account_id} (${rating.username}) WON: ${playerElo} → ${newElo} (+${delta}) ` +
        `[K=${K}, expected=${expected.toFixed(3)}, ${is1v1 ? "1v1" : "2v2"}]`,
      );
    }

    // Update each loser individually
    for (const rating of loserRatings) {
      const playerElo = rating[eloField];
      const totalGames = rating[winsField] + rating[lossesField];
      const K = getKFactor(totalGames);
      const expected = expectedScore(playerElo, avgWinnerElo);
      const delta = Math.round(K * (0 - expected));
      const newElo = Math.max(0, playerElo + delta);

      await EloRatingModel.updateOne(
        { account_id: rating.account_id },
        {
          $set: { [eloField]: newElo, updated_at: Date.now() },
          $inc: { [lossesField]: 1 },
        },
      );
      logger.info(
        `${logPrefix} ${rating.account_id} (${rating.username}) LOST: ${playerElo} → ${newElo} (${delta}) ` +
        `[K=${K}, expected=${expected.toFixed(3)}, ${is1v1 ? "1v1" : "2v2"}]`,
      );
    }

    logger.info(
      `${logPrefix} Processed match result for ${matchId}: team ${winningTeamIndex} won ` +
      `(avg ELO: winners=${Math.round(avgWinnerElo)} vs losers=${Math.round(avgLoserElo)})`,
    );
  } catch (error) {
    logger.error(`${logPrefix} Error processing match result for ${matchId}: ${error}`);
  }
}

/**
 * Process a match leave/disconnect — the leaver's team loses, the other team wins.
 * Uses the same dedup key as submit_end_of_match_stats so ELO is only processed once.
 *
 * @param matchId - The match ID
 * @param leaverPlayerId - The player who left/disconnected
 */
export async function processMatchLeave(matchId: string, leaverPlayerId: string): Promise<void> {
  try {
    // Same dedup key used by submit_end_of_match_stats
    const dedupeKey = `elo_processed:${matchId}`;
    const alreadyProcessed = await redisClient.set(dedupeKey, "1", { NX: true, EX: 300 });
    if (alreadyProcessed !== "OK") {
      logger.info(`${logPrefix} ELO already processed for match ${matchId}, skipping leave penalty`);
      return;
    }

    const matchConfig = await redisGetMatchConfig(matchId);
    if (!matchConfig || !matchConfig.players) {
      logger.warn(`${logPrefix} Cannot process match leave: match config not found for ${matchId}`);
      return;
    }

    // Find the leaver's team
    const leaver = matchConfig.players.find((p) => p.playerId === leaverPlayerId);
    if (!leaver) {
      logger.warn(`${logPrefix} Cannot process match leave: player ${leaverPlayerId} not found in match ${matchId}`);
      return;
    }

    // The opposing team wins
    const winningTeamIndex = leaver.teamIndex === 0 ? 1 : 0;
    logger.info(
      `${logPrefix} Processing match leave for ${matchId}: player ${leaverPlayerId} left (team ${leaver.teamIndex}), ` +
      `awarding win to team ${winningTeamIndex}`,
    );

    await processMatchResult(matchId, winningTeamIndex);
  } catch (error) {
    logger.error(`${logPrefix} Error processing match leave for ${matchId}: ${error}`);
  }
}

/**
 * Map ELO/RP to a ranked tier and division.
 * Tiers: Bronze(0-499), Silver(500-999), Gold(1000-1499), Platinum(1500-1999),
 *        Diamond(2000-2499), Master(2500-2999), Grandmaster(3000+)
 * Each tier has 5 divisions (1-5, where 5 is highest within the tier).
 */
export function eloToTierDivision(elo: number): { tier: string; division: number } {
  const tiers = [
    { name: "Bronze", min: 0, max: 499, divSize: 100 },
    { name: "Silver", min: 500, max: 999, divSize: 100 },
    { name: "Gold", min: 1000, max: 1499, divSize: 100 },
    { name: "Platinum", min: 1500, max: 1999, divSize: 100 },
    { name: "Diamond", min: 2000, max: 2499, divSize: 100 },
    { name: "Master", min: 2500, max: 2999, divSize: 100 },
    { name: "Grandmaster", min: 3000, max: Infinity, divSize: 100 },
  ];

  for (const tier of tiers) {
    if (elo >= tier.min && elo <= tier.max) {
      const division = Math.min(5, Math.floor((elo - tier.min) / tier.divSize) + 1);
      return { tier: tier.name, division };
    }
  }
  return { tier: "Bronze", division: 1 };
}

/**
 * Get a specific player's rank and stats for a given mode.
 * Returns null if the player hasn't played any games in this mode.
 */
export async function getPlayerRank(accountId: string, mode: "1v1" | "2v2") {
  const eloField = mode === "1v1" ? "elo_1v1" : "elo_2v2";
  const winsField = mode === "1v1" ? "wins_1v1" : "wins_2v2";
  const lossesField = mode === "1v1" ? "losses_1v1" : "losses_2v2";

  const player = await EloRatingModel.findOne({ account_id: accountId }).lean();
  if (!player) return null;

  const totalGames = (player[winsField] || 0) + (player[lossesField] || 0);
  if (totalGames === 0) return null;

  // Count how many players have a higher ELO (rank = that count + 1)
  const playersAbove = await EloRatingModel.countDocuments({
    [eloField]: { $gt: player[eloField] },
    $expr: { $gt: [{ $add: [`$${winsField}`, `$${lossesField}`] }, 0] },
  });

  // Find best character
  const charsField = mode === "1v1" ? "characters_1v1" : "characters_2v2";
  const charMap = (player as any)[charsField] || {};
  let bestChar = "";
  let bestCharElo = -1;
  for (const [slug, data] of Object.entries(charMap)) {
    const charElo = (data as any).elo || 0;
    if (charElo > bestCharElo) { bestCharElo = charElo; bestChar = slug; }
  }

  return {
    rank: playersAbove + 1,
    account_id: accountId,
    username: player.username || "Unknown",
    elo: player[eloField],
    wins: player[winsField] || 0,
    losses: player[lossesField] || 0,
    bestCharacter: bestChar,
  };
}

/**
 * Get leaderboard for a specific mode.
 * Returns top players sorted by ELO descending.
 * Only includes players who have played at least 1 game.
 */
export async function getLeaderboard(mode: "1v1" | "2v2", limit: number = 100) {
  const eloField = mode === "1v1" ? "elo_1v1" : "elo_2v2";
  const winsField = mode === "1v1" ? "wins_1v1" : "wins_2v2";
  const lossesField = mode === "1v1" ? "losses_1v1" : "losses_2v2";

  // Only show players who have played at least 1 game in this mode
  const players = await EloRatingModel.find({
    $expr: { $gt: [{ $add: [`$${winsField}`, `$${lossesField}`] }, 0] },
  })
    .sort({ [eloField]: -1 })
    .limit(limit)
    .lean();

  // Resolve missing usernames from Redis and backfill into MongoDB
  const results = await Promise.all(
    players.map(async (p, index) => {
      let username = p.username;
      if (!username || username === "Unknown") {
        // Try to resolve from Redis connection data
        username = await getPlayerUsername(p.account_id);
        // Also try the PlayerTester collection as fallback
        if (!username) {
          try {
            const playerDoc = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(p.account_id) });
            username = playerDoc?.name || "";
          } catch {
            // ignore lookup errors
          }
        }
        // Backfill the username into the ELO record so future queries have it
        if (username) {
          await EloRatingModel.updateOne(
            { account_id: p.account_id },
            { $set: { username } },
          );
        }
      }
      // Find best character (highest ELO) for this mode
      const charsField = mode === "1v1" ? "characters_1v1" : "characters_2v2";
      const charMap = (p as any)[charsField] || {};
      let bestChar = "";
      let bestCharElo = -1;
      for (const [slug, data] of Object.entries(charMap)) {
        const charElo = (data as any).elo || 0;
        if (charElo > bestCharElo) {
          bestCharElo = charElo;
          bestChar = slug;
        }
      }

      return {
        rank: index + 1,
        account_id: p.account_id,
        username: username || "Unknown",
        elo: p[eloField],
        wins: p[winsField],
        losses: p[lossesField],
        bestCharacter: bestChar,
      };
    }),
  );

  return results;
}
