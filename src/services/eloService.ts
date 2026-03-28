import { EloRatingModel } from "../database/EloRating";
import { redisGetMatchConfig, redisGetMatch, redisClient, RedisPlayerConnection } from "../config/redis";
import { logger } from "../config/logger";
import { PlayerTesterModel } from "../database/PlayerTester";
import { Types } from "mongoose";
import env from "../env/env";

const serviceName = "Services.Elo";
const logPrefix = `[${serviceName}]:`;

const DEFAULT_ELO = env.DEFAULT_ELO || 1000;

// --- Dynamic K-Factor Configuration ---
// Provisional: first N games use higher K for faster convergence
const PROVISIONAL_GAME_THRESHOLD = Number(env.PROVISIONAL_GAME_THRESHOLD) || 20;
const K_PROVISIONAL = Number(env.K_PROVISIONAL) || 64;
const K_1V1 = Number(env.K_1V1) || 32;
const K_2V2 = Number(env.K_2V2) || 24; // Lower K for 2v2 due to higher variance
const ELO_DIVISOR = Number(env.ELO_DIVISOR) || 800; // Standard ELO divisor for expected score calculation

/**
 * Get the dynamic K-factor for a player based on mode and game count.
 */
function getKFactor(totalGames: number, is1v1: boolean): number {
  if (totalGames < PROVISIONAL_GAME_THRESHOLD) {
    return K_PROVISIONAL;
  }
  return is1v1 ? K_1V1 : K_2V2;
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
 * Look up a player's character from Redis (set by lock_lobby_loadout SSC).
 * Fallback chain: player:{id} → connections:{id} → MongoDB PlayerTester.
 */
async function getPlayerCharacter(playerId: string): Promise<string> {
  try {
    // Try player:{id} first (set by lock_lobby_loadout)
    const playerData = await redisClient.hGetAll(`player:${playerId}`);
    if (playerData?.character) return playerData.character;

    // Fallback to connections:{id}
    const connData = await redisClient.hGetAll(`connections:${playerId}`);
    if (connData?.character) return connData.character;

    // Fallback to MongoDB
    const dbPlayer = await PlayerTesterModel.findById(playerId).lean();
    if (dbPlayer && (dbPlayer as any).character) return (dbPlayer as any).character;
  } catch {
    // ignore
  }
  return "";
}

/**
 * Compute the most-played character from a character_counts map.
 */
function getTopCharacter(counts: Record<string, number>): string {
  let top = "";
  let topCount = 0;
  for (const [char, count] of Object.entries(counts)) {
    if (count > topCount) {
      topCount = count;
      top = char;
    }
  }
  return top;
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
    const charCountsField = is1v1 ? "character_counts_1v1" : "character_counts_2v2";
    const topCharField = is1v1 ? "top_character_1v1" : "top_character_2v2";

    // Get all player ratings, usernames, and characters from Redis
    const winnerRatings = await Promise.all(
      winners.map(async (p) => {
        const username = await getPlayerUsername(p.playerId);
        const character = await getPlayerCharacter(p.playerId);
        return { rating: await getOrCreateRating(p.playerId, username), character };
      }),
    );
    const loserRatings = await Promise.all(
      losers.map(async (p) => {
        const username = await getPlayerUsername(p.playerId);
        const character = await getPlayerCharacter(p.playerId);
        return { rating: await getOrCreateRating(p.playerId, username), character };
      }),
    );

    // Calculate opposing team averages
    const avgWinnerElo = winnerRatings.reduce((sum, { rating: r }) => sum + r[eloField], 0) / winnerRatings.length;
    const avgLoserElo = loserRatings.reduce((sum, { rating: r }) => sum + r[eloField], 0) / loserRatings.length;

    // --- Per-player ELO updates ---
    // Each player's expected score is computed against the opposing TEAM average.
    // This means a high-rated player on the winning team gains LESS (their expected
    // score vs a weak team is already high) while a low-rated player gains MORE.
    // This prevents boosting and provides fair individual calibration.

    // Update each winner individually
    for (const { rating, character } of winnerRatings) {
      const playerElo = rating[eloField];
      const totalGames = rating[winsField] + rating[lossesField];
      const K = getKFactor(totalGames, is1v1);
      const expected = expectedScore(playerElo, avgLoserElo);
      const delta = Math.round(K * (1 - expected));
      const newElo = Math.max(0, playerElo + delta);

      // Update character counts
      const charCounts = { ...(rating[charCountsField] || {}) };
      if (character) {
        charCounts[character] = (charCounts[character] || 0) + 1;
      }
      const topChar = getTopCharacter(charCounts);

      await EloRatingModel.updateOne(
        { account_id: rating.account_id },
        {
          $set: {
            [eloField]: newElo,
            [charCountsField]: charCounts,
            [topCharField]: topChar,
            updated_at: Date.now(),
          },
          $inc: { [winsField]: 1 },
        },
      );
      logger.info(
        `${logPrefix} ${rating.account_id} (${rating.username}) WON: ${playerElo} → ${newElo} (+${delta}) ` +
        `[K=${K}, expected=${expected.toFixed(3)}, ${is1v1 ? "1v1" : "2v2"}, char=${character || "unknown"}]`,
      );
    }

    // Update each loser individually
    for (const { rating, character } of loserRatings) {
      const playerElo = rating[eloField];
      const totalGames = rating[winsField] + rating[lossesField];
      const K = getKFactor(totalGames, is1v1);
      const expected = expectedScore(playerElo, avgWinnerElo);
      const delta = Math.round(K * (0 - expected));
      const newElo = Math.max(0, playerElo + delta);

      // Update character counts
      const charCounts = { ...(rating[charCountsField] || {}) };
      if (character) {
        charCounts[character] = (charCounts[character] || 0) + 1;
      }
      const topChar = getTopCharacter(charCounts);

      await EloRatingModel.updateOne(
        { account_id: rating.account_id },
        {
          $set: {
            [eloField]: newElo,
            [charCountsField]: charCounts,
            [topCharField]: topChar,
            updated_at: Date.now(),
          },
          $inc: { [lossesField]: 1 },
        },
      );
      logger.info(
        `${logPrefix} ${rating.account_id} (${rating.username}) LOST: ${playerElo} → ${newElo} (${delta}) ` +
        `[K=${K}, expected=${expected.toFixed(3)}, ${is1v1 ? "1v1" : "2v2"}, char=${character || "unknown"}]`,
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

  const topCharField = mode === "1v1" ? "top_character_1v1" : "top_character_2v2";
  return {
    rank: playersAbove + 1,
    account_id: accountId,
    username: player.username || "Unknown",
    elo: player[eloField],
    wins: player[winsField] || 0,
    losses: player[lossesField] || 0,
    top_character: player[topCharField] || "",
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
      const topCharField2 = mode === "1v1" ? "top_character_1v1" : "top_character_2v2";
      return {
        rank: index + 1,
        account_id: p.account_id,
        username: username || "Unknown",
        elo: p[eloField],
        wins: p[winsField],
        losses: p[lossesField],
        top_character: p[topCharField2] || "",
      };
    }),
  );

  return results;
}
