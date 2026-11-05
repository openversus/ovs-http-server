import { PlayerRewardTrackState, PlayerRewardTrackStateModel } from "../database/PlayerRewardTrackStates";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "./milestones";
import { ACTIVE_BP_TRACK_GUID, deriveTierState, getTiersForTrack } from "./rewardTracks";

export const ACTIVE_BP_TRACK_SLUG = "mrt_battlepass_season_five";

// Zero-start, read-existing-score behavior. Tier arrays are derived from the
// saved score so saved progress follows any change to the tier layout.
export function formatBattlepassState(state?: Partial<PlayerRewardTrackState> | null) {
  const currentScore = state?.currentScore ?? 0;
  // Progress follows the score; claims are the player's own (claimed from the pass screen).
  const derived = state ? deriveTierState(currentScore, getTiersForTrack(ACTIVE_BP_TRACK_SLUG) ?? [], false) : null;
  return {
    TrackSlug: ACTIVE_BP_TRACK_SLUG,
    RewardTrackClass: "MvsBattlepassRewardTrackHsda",
    CurrentScore: currentScore,
    CurrentTier: derived?.currentTier ?? 0,
    CompletedTiers: derived?.completedTiers ?? [],
    ClaimedRewards: state?.claimedRewards ?? [],
    bHasPremium: state?.bHasPremium ?? false,
    Guid: ACTIVE_BP_TRACK_GUID,
    InfiniteTierThreshold: OVS_BATTLEPASS_REWARD_SLUGS.length - 1,
    HighestClaimedInifiniteTier: -1,
  };
}

export async function getActiveBattlepassState(accountId?: string) {
  if (!accountId) return formatBattlepassState();
  const state = await PlayerRewardTrackStateModel.findOne({ accountId, trackSlug: ACTIVE_BP_TRACK_SLUG }).lean().exec();
  return formatBattlepassState(state);
}
