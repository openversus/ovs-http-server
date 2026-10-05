import { logger } from "../config/logger";
import { redisClient } from "../config/redis";

const logPrefix = "[Services.RankedSetXp]:";
// The C# match flow pays it (RankedSetXpSubscriber, dotnet/docs/MIGRATION-BRIDGES.md 9): the battle pass, the account
// level and the played character's level (its Fighter Pass), into the reward tracks the C# server keeps.
export const RANKED_SET_XP_CHANNEL = "reward_tracks:ranked_set";

export interface RankedSetResult {
  winnerIds: string[];
  loserIds: string[];
  /** playerId -> character slug for the set, exactly as the client sent it. */
  playerCharacters: Map<string, string>;
  /** Stable id for the set (set id or its deciding match id); used to grant once. */
  setKey: string;
  /** Games won by each team in the set; their sum is the number of games played. */
  setScore: [number, number];
  isConcede: boolean;
  isPregameDodge: boolean;
}

/**
 * Pay one player for one ranked set or public FFA game: battle-pass XP, plus
 * Fighter Pass XP on the character they played. C# pays it once per `setKey`
 * and player.
 */
async function grantPlayerXp(accountId: string, won: boolean, characterSlug: string, setKey: string, source: string) {
  await redisClient.publish(RANKED_SET_XP_CHANNEL, JSON.stringify({ playerId: accountId, won, character: characterSlug, setKey, source }));
  logger.info(`${logPrefix} ${accountId}: ${won ? "win" : "loss"} as ${characterSlug} (${source}) sent for payment`);
}

/**
 * Battle-pass and Fighter Pass XP are earned once per completed ranked set,
 * never per game. Custom games never create ranked sets, so they earn neither.
 * A pregame dodge (no game played) earns nothing. A concede (which includes the
 * loser walking out of the results screen before the set is settled) still pays
 * the losing side once at least one game was played; a concede before any game
 * finished pays only the side that stayed, so quitting is never a shortcut.
 * Fighter Pass XP goes to the character the player used for the set.
 */
export async function awardRankedSetXp(result: RankedSetResult): Promise<void> {
  if (result.isPregameDodge) return;
  const gamesPlayed = result.setScore[0] + result.setScore[1];
  const payLosers = !result.isConcede || gamesPlayed > 0;
  const recipients = [
    ...result.winnerIds.map((id) => ({ id, won: true })),
    ...(payLosers ? result.loserIds.map((id) => ({ id, won: false })) : []),
  ];
  for (const { id, won } of recipients) {
    try {
      await grantPlayerXp(
        id, won, result.playerCharacters.get(id) || "",
        `ranked:${result.setKey}`,
        `ranked set ${won ? "win" : "loss"} ${result.setKey}`,
      );
    } catch (error) {
      logger.error(`${logPrefix} Ranked set XP grant failed for ${id} in set ${result.setKey}: ${error}`);
    }
  }
}

/**
 * Public FFA has no sets, so each game pays like a set: 600 to the winner,
 * 400 to everyone else. Custom FFA lobbies never call this.
 */
export async function awardFfaMatchXp(accountId: string, won: boolean, characterSlug: string, matchId: string): Promise<void> {
  try {
    await grantPlayerXp(accountId, won, characterSlug, `ffa:${matchId}`, `FFA ${won ? "win" : "game"} ${matchId}`);
  } catch (error) {
    logger.error(`${logPrefix} FFA XP grant failed for ${accountId} in ${matchId}: ${error}`);
  }
}
