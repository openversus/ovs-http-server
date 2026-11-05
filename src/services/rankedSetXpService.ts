import { logger } from "../config/logger";
import { redisClient } from "../config/redis";

const logPrefix = "[Services.RankedSetXp]:";
// The C# match flow pays it (RankedSetXpSubscriber, dotnet/docs/MIGRATION-BRIDGES.md 10): the battle pass, the account
// level and the played character's level (its Fighter Pass), into the reward tracks the C# server keeps.
export const RANKED_SET_XP_CHANNEL = "reward_tracks:ranked_set";

export interface RankedSetResult {
  winnerIds: string[];
  loserIds: string[];
  /** playerId -> character slug for the set, exactly as the client sent it. */
  playerCharacters: Map<string, string>;
  /** Stable id for the set (set id or its deciding match id); used to grant once. */
  setKey: string;
  /** Games the set finished. A dodge's score is [0, 0], so it counts the games before the dodge. */
  gamesPlayed: number;
  /** The set ended by a concede (or a walkout or dodge recorded as one). */
  isConcede: boolean;
  /** Who quit (conceded, walked out or dodged). A concede that names nobody counts every loser as a quitter. */
  quitterIds: string[];
}

/** Games a ranked set has finished so far (0 when there is no set state), for a dodge before game 2 or 3. */
export async function gamesFinishedInSet(setId: string): Promise<number> {
  const raw = await redisClient.get(`ranked_set:${setId}`);
  if (!raw) return 0;
  const set = JSON.parse(raw);
  const scores: number[] = set.scores || [0, 0];
  return Math.max(Number(set.gamesPlayed) || 0, (scores[0] || 0) + (scores[1] || 0));
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
 * XP needs at least one game played: a set that ends before any game finished
 * (a pregame dodge) pays nobody, not even the side given the win. After that,
 * whoever quit (a concede, a walkout, or a dodge before game 2 or 3) gets
 * nothing, and everyone else is paid: the winners aren't at fault, and neither
 * is a 2v2 teammate who stayed. Fighter Pass XP goes to the character the
 * player used for the set.
 */
export async function awardRankedSetXp(result: RankedSetResult): Promise<void> {
  if (result.gamesPlayed < 1) {
    logger.info(`${logPrefix} Set ${result.setKey} ended before any game was played: no XP`);
    return;
  }
  const quitters = new Set(result.quitterIds.length ? result.quitterIds : result.isConcede ? result.loserIds : []);
  const recipients = [
    ...result.winnerIds.map((id) => ({ id, won: true })),
    ...result.loserIds.map((id) => ({ id, won: false })),
  ].filter(({ id }) => !quitters.has(id));
  if (quitters.size) logger.info(`${logPrefix} Set ${result.setKey}: no XP for ${[...quitters].join(", ")} (quit)`);
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
