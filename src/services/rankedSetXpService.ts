import { logger } from "../config/logger";
import { redisClient, redisPublishRewardTrackUpdate } from "../config/redis";
import { getChromiumMasteryTrackForCharacter } from "../data/chromiumSkins";
import {
  ACTIVE_BP_TRACK_SLUG,
  advanceTrack,
  getRankedSetXp,
  getTrackPushFields,
  grantCrossedTierRewards,
} from "../data/rewardTracks";

const logPrefix = "[Services.RankedSetXp]:";
const GRANT_DEDUP_TTL_SECONDS = 7 * 24 * 60 * 60;

/** Add XP to a reward track and push the new state to the player's client. */
export async function advanceTrackAndPublish(accountId: string, trackSlug: string, xpDelta: number, source: string) {
  const { state, previousScore, previousTier, tierUp } = await advanceTrack(accountId, xpDelta, trackSlug);
  await redisPublishRewardTrackUpdate({
    accountId,
    trackSlug,
    currentScore: state.currentScore,
    currentTier: state.currentTier,
    completedTiers: state.completedTiers,
    claimedRewards: state.claimedRewards,
    bHasPremium: trackSlug === ACTIVE_BP_TRACK_SLUG ? state.bHasPremium : false,
    updateContext: 6,
    xpDelta,
    ...getTrackPushFields(trackSlug),
  });
  logger.info(`${logPrefix} ${trackSlug} ${accountId}: +${xpDelta} (${source}), tier ${previousTier}->${state.currentTier}${tierUp ? " TIER UP" : ""}`);
  return { state, previousScore };
}

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
 * Fighter Pass XP on the character they played. `dedupKey` makes it pay once.
 */
async function grantPlayerXp(accountId: string, won: boolean, characterSlug: string, dedupKey: string, source: string) {
  const claimed = await redisClient.set(dedupKey, "1", { NX: true, EX: GRANT_DEDUP_TTL_SECONDS });
  if (claimed !== "OK") return;

  const xp = getRankedSetXp(won);
  await advanceTrackAndPublish(accountId, ACTIVE_BP_TRACK_SLUG, xp, source);

  const trackSlug = getChromiumMasteryTrackForCharacter(characterSlug);
  if (!trackSlug) {
    logger.warn(`${logPrefix} No Fighter Pass for character '${characterSlug}' (${accountId}); ${source}`);
    return;
  }
  const { state, previousScore } = await advanceTrackAndPublish(accountId, trackSlug, xp, `${source} as ${characterSlug}`);
  // Crossed tiers pay out toasts; tiers 5, 10 and 15 feed the battle pass.
  const { battlepassXp } = await grantCrossedTierRewards(accountId, trackSlug, previousScore, state.currentScore);
  if (battlepassXp > 0) {
    await advanceTrackAndPublish(accountId, ACTIVE_BP_TRACK_SLUG, battlepassXp, `${trackSlug} tier reward`);
  }
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
        `ranked_set_xp_granted:${result.setKey}:${id}`,
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
    await grantPlayerXp(accountId, won, characterSlug, `ffa_xp_granted:${matchId}:${accountId}`, `FFA ${won ? "win" : "game"} ${matchId}`);
  } catch (error) {
    logger.error(`${logPrefix} FFA XP grant failed for ${accountId} in ${matchId}: ${error}`);
  }
}
