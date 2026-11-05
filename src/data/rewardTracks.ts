import { logger } from "../config/logger";
import { PlayerRewardTrackState, PlayerRewardTrackStateModel } from "../database/PlayerRewardTrackStates";
import {
  CHROMIUM_MASTERY_TRACK_SLUGS,
  CHROMIUM_SKIN_BY_MASTERY_TRACK,
  CHROMIUM_SKIN_SLUG_SET,
} from "./chromiumSkins";
import { MILESTONE_REWARDS } from "./milestones";
import { RECOVERED_EMOTE_SLUG_SET } from "./recoveredEmotes";
import { PINK_MOUNTAINEER_JASON_SLUG } from "./pinkMountaineerJason";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "./milestones";
import { battlepassXpForReward, FIGHTER_PASS_TOAST_SLUG } from "./fighterPass";
import { adjustMatchToasts } from "./playerCounters";
import { getStaticTrackGuid } from "./rewardTrackStates";

const serviceName = "Data.RewardTracks";
const logPrefix = `[${serviceName}]:`;

export const ACTIVE_BP_TRACK_SLUG = "mrt_battlepass_season_five";
/** The battle pass's Guid in get_milestone_reward_tracks; live updates must match it. */
export const ACTIVE_BP_TRACK_GUID = "e693b965-7c8c-40f7-b89f-14c79dd6a609";
// Battle pass and Fighter Pass XP are both earned once per completed ranked set.
export const SET_COMPLETION_XP = 400;
export const SET_WIN_BONUS_XP = 200;

export const BATTLEPASS_AUTO_GRANT_SLUG_SET = new Set<string>([
  ...CHROMIUM_SKIN_SLUG_SET,
  ...RECOVERED_EMOTE_SLUG_SET,
  PINK_MOUNTAINEER_JASON_SLUG,
  ...OVS_BATTLEPASS_REWARD_SLUGS,
]);

type TierReward = {
  RewardGuid: string;
  InventoryHsda?: string;
  RewardHsda?: string;
  DirectInventoryItemCount?: number;
};

type TierDef = {
  ScoreThreshold: number;
  TierGuid: string;
  Rewards: TierReward[];
};

/** Rewards the server delivers itself; they are marked claimed as soon as the tier completes. */
export function isServerGrantedReward(reward: TierReward): boolean {
  if (reward.InventoryHsda) {
    return reward.InventoryHsda === FIGHTER_PASS_TOAST_SLUG || BATTLEPASS_AUTO_GRANT_SLUG_SET.has(reward.InventoryHsda);
  }
  return battlepassXpForReward(reward.RewardHsda) > 0;
}

export function getTiersForTrack(trackSlug: string): TierDef[] | null {
  const track = (MILESTONE_REWARDS as any)[trackSlug];
  if (!track?.data?.Tiers) return null;
  return [...track.data.Tiers].sort((a, b) => a.ScoreThreshold - b.ScoreThreshold);
}

/**
 * Battle-pass rewards are claimed by the player from the pass screen; every
 * other track (Fighter Passes) is paid out by the server as tiers complete.
 */
export function isManualClaimTrack(trackSlug: string): boolean {
  return trackSlug === ACTIVE_BP_TRACK_SLUG;
}

export function deriveTierState(score: number, tiers: TierDef[], autoClaim = true) {
  const completedTiers: string[] = [];
  const claimedRewards: string[] = [];
  for (let i = 0; i < tiers.length && tiers[i].ScoreThreshold <= score; i++) {
    completedTiers.push(tiers[i].TierGuid);
    if (!autoClaim) continue;
    claimedRewards.push(...tiers[i].Rewards
      .filter(isServerGrantedReward)
      .map((reward) => reward.RewardGuid));
  }
  // The client treats CurrentTier as the tier being worked towards and shows
  // "Tiers[CurrentTier] - score for Tier CurrentTier+1". Using the last
  // completed tier instead made the match-end banner jump to a negative value.
  // Capped at the last tier so a finished track never indexes past the end.
  const currentTier = Math.max(0, Math.min(completedTiers.length, tiers.length - 1));
  return { currentTier, completedTiers, claimedRewards };
}

/** XP a completed ranked set earns on the battle pass and on the Fighter Pass alike. */
export function getRankedSetXp(won: boolean) {
  return SET_COMPLETION_XP + (won ? SET_WIN_BONUS_XP : 0);
}

export async function getOrCreateTrackState(accountId: string, trackSlug = ACTIVE_BP_TRACK_SLUG) {
  const tiers = getTiersForTrack(trackSlug);
  if (!tiers) throw new Error(`Unknown reward track ${trackSlug}`);
  const initial = deriveTierState(0, tiers, !isManualClaimTrack(trackSlug));
  return PlayerRewardTrackStateModel.findOneAndUpdate(
    { accountId, trackSlug },
    { $setOnInsert: { accountId, trackSlug, currentScore: 0, ...initial, bHasPremium: false } },
    { upsert: true, new: true },
  ).lean() as unknown as Promise<PlayerRewardTrackState>;
}

export async function advanceTrack(accountId: string, xpDelta: number, trackSlug = ACTIVE_BP_TRACK_SLUG) {
  if (!Number.isFinite(xpDelta) || xpDelta <= 0) {
    const state = await getOrCreateTrackState(accountId, trackSlug);
    return { state, previousScore: state.currentScore, previousTier: state.currentTier, tierUp: false };
  }
  const tiers = getTiersForTrack(trackSlug);
  if (!tiers) throw new Error(`Unknown reward track ${trackSlug}`);
  const before = await getOrCreateTrackState(accountId, trackSlug);
  const incremented = await PlayerRewardTrackStateModel.findOneAndUpdate(
    { accountId, trackSlug },
    { $inc: { currentScore: xpDelta } },
    { new: true },
  ).lean() as any;
  const previousScore = incremented.currentScore - xpDelta;
  const manualClaim = isManualClaimTrack(trackSlug);
  const derived = deriveTierState(incremented.currentScore, tiers, !manualClaim);
  // Manual-claim tracks keep the player's own claims; only progress moves.
  const progress = manualClaim
    ? { currentTier: derived.currentTier, completedTiers: derived.completedTiers }
    : derived;
  const state = await PlayerRewardTrackStateModel.findOneAndUpdate(
    { accountId, trackSlug },
    { $set: progress },
    { new: true },
  ).lean() as unknown as PlayerRewardTrackState;
  logger.info(`${logPrefix} ${accountId} +${xpDelta} XP; score=${state.currentScore}, tier=${before.currentTier}->${state.currentTier}`);
  return { state, previousScore, previousTier: before.currentTier, tierUp: state.currentTier > before.currentTier };
}

/** Toasts and battle-pass XP carried by the tiers crossed moving from `fromScore` to `toScore`. */
export function getCrossedTierGrants(trackSlug: string, fromScore: number, toScore: number) {
  let toasts = 0;
  let battlepassXp = 0;
  for (const tier of getTiersForTrack(trackSlug) ?? []) {
    if (tier.ScoreThreshold <= fromScore || tier.ScoreThreshold > toScore) continue;
    for (const reward of tier.Rewards) {
      if (reward.InventoryHsda === FIGHTER_PASS_TOAST_SLUG) toasts += reward.DirectInventoryItemCount ?? 0;
      battlepassXp += battlepassXpForReward(reward.RewardHsda);
    }
  }
  return { toasts, battlepassXp };
}

/**
 * Deliver the toast rewards of newly crossed Fighter Pass tiers. Battle-pass XP
 * is returned for the caller to apply to the active pass (and push to the client).
 * The Chromium capstone needs no grant: ownership is read from the track score.
 */
export async function grantCrossedTierRewards(accountId: string, trackSlug: string, fromScore: number, toScore: number) {
  const grants = getCrossedTierGrants(trackSlug, fromScore, toScore);
  if (grants.toasts > 0) {
    const balance = await adjustMatchToasts(accountId, grants.toasts);
    logger.info(`${logPrefix} ${accountId} ${trackSlug}: +${grants.toasts} toasts from tiers (balance ${balance})`);
  }
  return grants;
}

/**
 * Class, infinite-tier threshold and Guid for realtime pushes, matching what
 * get_milestone_reward_tracks served. The client ignored pushes whose Guid did
 * not match (all-zero Guid vs the served one): the battle pass never moved live
 * and only tracks served with an empty Guid (e.g. Banana Guard) showed a banner.
 */
export function getTrackPushFields(trackSlug: string) {
  const track = (MILESTONE_REWARDS as any)[trackSlug];
  return {
    rewardTrackClass: String(track?.data?.RewardTrackClass ?? "MvsEventRewardTrackHsda"),
    infiniteTierThreshold: (track?.data?.Tiers?.length ?? 0) - 1,
    guid: trackSlug === ACTIVE_BP_TRACK_SLUG ? ACTIVE_BP_TRACK_GUID : getStaticTrackGuid(trackSlug),
  };
}

export async function getOwnedBattlepassSlugs(accountId: string): Promise<Set<string>> {
  const state = await PlayerRewardTrackStateModel.findOne({ accountId, trackSlug: ACTIVE_BP_TRACK_SLUG }).lean().exec();
  if (!state) return new Set();
  // Battle-pass items are owned once claimed (and a claim needs the tier reached).
  const claimed = new Set(state.claimedRewards ?? []);
  const owned = new Set<string>();
  for (const tier of getTiersForTrack(ACTIVE_BP_TRACK_SLUG) ?? []) {
    if (tier.ScoreThreshold > state.currentScore) break;
    for (const reward of tier.Rewards) {
      if (reward.InventoryHsda && claimed.has(reward.RewardGuid) && BATTLEPASS_AUTO_GRANT_SLUG_SET.has(reward.InventoryHsda)) {
        owned.add(reward.InventoryHsda);
      }
    }
  }
  return owned;
}

/**
 * Claim reached tiers on a manual-claim track: all of them, or only `tierGuids`.
 * Returns the saved state and the rewards claimed by this call (for the
 * OnRewardsGranted popup). Claiming twice is a no-op.
 */
export async function claimTrackTiers(accountId: string, trackSlug: string, tierGuids?: string[]) {
  const tiers = getTiersForTrack(trackSlug);
  if (!tiers || !isManualClaimTrack(trackSlug)) return { state: null, claimed: [] as TierReward[] };
  const state = await getOrCreateTrackState(accountId, trackSlug);
  const alreadyClaimed = new Set(state.claimedRewards ?? []);
  const wanted = tierGuids?.length ? new Set(tierGuids) : null;
  const claimed: TierReward[] = [];
  for (const tier of tiers) {
    if (tier.ScoreThreshold > state.currentScore) break;
    if (wanted && !wanted.has(tier.TierGuid)) continue;
    for (const reward of tier.Rewards) {
      if (!alreadyClaimed.has(reward.RewardGuid)) claimed.push(reward);
    }
  }
  if (claimed.length === 0) return { state, claimed };
  const updated = await PlayerRewardTrackStateModel.findOneAndUpdate(
    { accountId, trackSlug },
    { $addToSet: { claimedRewards: { $each: claimed.map((reward) => reward.RewardGuid) } } },
    { new: true },
  ).lean() as unknown as PlayerRewardTrackState;
  logger.info(`${logPrefix} ${accountId} claimed ${claimed.length} reward(s) on ${trackSlug}`);
  return { state: updated, claimed };
}

export function formatCharacterMasteryState(trackSlug: string, state?: Partial<PlayerRewardTrackState> | null) {
  const tiers = getTiersForTrack(trackSlug) ?? [];
  // Derived from the score rather than the saved arrays, so saved progress
  // follows any change to the tier thresholds.
  const currentScore = state?.currentScore ?? 0;
  const derived = deriveTierState(currentScore, tiers);
  return {
    TrackSlug: trackSlug,
    RewardTrackClass: "MvsCharacterMasteryRewardTrackHsda",
    CurrentScore: currentScore,
    CurrentTier: derived.currentTier,
    CompletedTiers: derived.completedTiers,
    ClaimedRewards: derived.claimedRewards,
    bHasPremium: false,
    InfiniteTierThreshold: tiers.length - 1,
    HighestClaimedInifiniteTier: -1,
  };
}

/** Read every Chromium-bearing Fighter Pass in one query for the HISS state response. */
export async function getCharacterMasteryStates(accountId?: string) {
  if (!accountId) {
    return CHROMIUM_MASTERY_TRACK_SLUGS.map((trackSlug) => formatCharacterMasteryState(trackSlug));
  }
  const saved = await PlayerRewardTrackStateModel.find({
    accountId,
    trackSlug: { $in: CHROMIUM_MASTERY_TRACK_SLUGS },
  }).lean().exec();
  const byTrack = new Map(saved.map((state) => [state.trackSlug, state]));
  return CHROMIUM_MASTERY_TRACK_SLUGS.map((trackSlug) =>
    formatCharacterMasteryState(trackSlug, byTrack.get(trackSlug)),
  );
}

/** Resolve Chromium ownership from the matching character-mastery score. */
export async function getOwnedFighterPassSlugs(accountId: string): Promise<Set<string>> {
  const states = await PlayerRewardTrackStateModel.find({
    accountId,
    trackSlug: { $in: CHROMIUM_MASTERY_TRACK_SLUGS },
  }).lean().exec();
  const owned = new Set<string>();
  for (const state of states) {
    const skinSlug = CHROMIUM_SKIN_BY_MASTERY_TRACK.get(state.trackSlug);
    const tiers = getTiersForTrack(state.trackSlug);
    const capstone = tiers?.[tiers.length - 1];
    if (skinSlug && capstone && state.currentScore >= capstone.ScoreThreshold) {
      owned.add(skinSlug);
    }
  }
  return owned;
}
