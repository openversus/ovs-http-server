// Per-player per-track progression state.
//
// One doc per (accountId, trackSlug) pair, unique-indexed on that compound
// key. Stores the persistent state the game expects in
// /ssc/invoke/get_milestone_reward_tracks: current score, current tier,
// list of completed tier GUIDs, list of claimed reward GUIDs, and the
// premium flag.
//
// Why a sidecar collection rather than nesting into Player?
//   - Tracks are independent; an $inc on one player+track shouldn't touch
//     unrelated state on the Player blob.
//   - Mastery tracks (mrt_mastery_*) eventually multiply this by ~30
//     characters; keeping it flat lets us read/write a single track without
//     hydrating everything else.
//   - Same pattern as PlayerCounters — small, hot-path-friendly.

import { getModelForClass, modelOptions, prop, Severity, index } from "@typegoose/typegoose";

@index({ accountId: 1, trackSlug: 1 }, { unique: true })
@modelOptions({ options: { allowMixed: Severity.ALLOW }, schemaOptions: { timestamps: true } })
export class PlayerRewardTrackState {
  // Player.id (the public string id, not Mongo _id).
  @prop({ required: true, index: true })
  public accountId!: string;

  // Track slug from MILESTONE_REWARDS — e.g. "mrt_battlepass_season_five",
  // "mrt_mastery_velma", etc.
  @prop({ required: true, index: true })
  public trackSlug!: string;

  // Total accumulated score on this track. Tier is derived from this by
  // walking MILESTONE_REWARDS[trackSlug].data.Tiers in ascending threshold
  // order — every tier whose ScoreThreshold <= CurrentScore is "completed."
  @prop({ required: true, default: 0 })
  public currentScore!: number;

  // Highest tier number reached (0-indexed). Derived field; we persist it
  // for fast reads and to avoid recomputing on every fetch.
  @prop({ required: true, default: 0 })
  public currentTier!: number;

  // TierGuid strings the player has completed (crossed the threshold of).
  // The game expects these as a flat string array. Order matches tier order.
  @prop({ required: true, default: [], type: () => [String] })
  public completedTiers!: string[];

  // RewardGuid strings the player has actually claimed. A tier can be
  // completed without its rewards being claimed yet (the player has to
  // open the BP UI and hit claim). Phase 3 work — for now this stays empty.
  @prop({ required: true, default: [], type: () => [String] })
  public claimedRewards!: string[];

  // Premium BP ownership for this track. Drives whether premium-tier
  // rewards are claimable. Phase 4 — for now defaults false.
  @prop({ required: true, default: false })
  public bHasPremium!: boolean;
}

export const PlayerRewardTrackStateModel = getModelForClass(PlayerRewardTrackState);
