import assert from "node:assert/strict";
import test from "node:test";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { redisClient } from "../src/config/redis";
import { handleSsc_invoke_claim_all_milestone_reward_track_tiers } from "../src/handlers/ssc";
import { MILESTONE_REWARDS } from "../src/data/milestones";
import {
  ACTIVE_BP_TRACK_SLUG,
  advanceTrack,
  claimTrackTiers,
  getOwnedBattlepassSlugs,
} from "../src/data/rewardTracks";

const tiers = MILESTONE_REWARDS.mrt_battlepass_season_five.data.Tiers as any[];

// One in-memory battle-pass document behind both findOneAndUpdate and findOne.
function store(t: any, initial: Record<string, any> | null) {
  let doc = initial ? { ...initial } : null;
  t.mock.method(PlayerRewardTrackStateModel, "findOneAndUpdate", (_filter: any, update: any) => ({
    lean: async () => {
      if (!doc && update.$setOnInsert) doc = { ...update.$setOnInsert };
      if (update.$inc) doc!.currentScore += update.$inc.currentScore;
      if (update.$set) doc = { ...doc!, ...update.$set };
      if (update.$addToSet) {
        const add = update.$addToSet.claimedRewards.$each as string[];
        doc!.claimedRewards = [...new Set([...(doc!.claimedRewards ?? []), ...add])];
      }
      return doc && { ...doc };
    },
  }) as any);
  t.mock.method(PlayerRewardTrackStateModel, "findOne", () => ({ lean: () => ({ exec: async () => doc && { ...doc } }) }) as any);
  return () => doc!;
}

test("battle-pass progress no longer auto-claims; items are owned only after claiming", async (t) => {
  const current = store(t, null);
  await advanceTrack("acct", 4_200, ACTIVE_BP_TRACK_SLUG); // reaches tiers 1-3
  assert.equal(current().completedTiers.length, 3);
  assert.deepEqual(current().claimedRewards, []);
  assert.deepEqual([...await getOwnedBattlepassSlugs("acct")], []);

  // Claim only tier 2, then everything that is reached.
  const one = await claimTrackTiers("acct", ACTIVE_BP_TRACK_SLUG, [tiers[1].TierGuid]);
  assert.deepEqual(one.claimed.map(r => r.InventoryHsda), [tiers[1].Rewards[0].InventoryHsda]);
  assert.deepEqual([...await getOwnedBattlepassSlugs("acct")], [tiers[1].Rewards[0].InventoryHsda]);

  const all = await claimTrackTiers("acct", ACTIVE_BP_TRACK_SLUG);
  assert.deepEqual(all.claimed.map(r => r.InventoryHsda), [tiers[0], tiers[2]].map(x => x.Rewards[0].InventoryHsda));
  assert.equal((await getOwnedBattlepassSlugs("acct")).size, 3);

  // Claiming again grants nothing; a later level-up keeps the earlier claims.
  assert.equal((await claimTrackTiers("acct", ACTIVE_BP_TRACK_SLUG)).claimed.length, 0);
  await advanceTrack("acct", 2_000, ACTIVE_BP_TRACK_SLUG);
  assert.equal(current().claimedRewards.length, 3);
  assert.equal(current().completedTiers.length, 4);
});

test("a tier that is not reached yet cannot be claimed", async (t) => {
  store(t, { accountId: "acct", trackSlug: ACTIVE_BP_TRACK_SLUG, currentScore: 100, claimedRewards: [] });
  const result = await claimTrackTiers("acct", ACTIVE_BP_TRACK_SLUG, [tiers[5].TierGuid]);
  assert.equal(result.claimed.length, 0);
});

test("a claim pushes OnRewardsGranted and a RewardTrackClaim track update with the battle-pass Guid", async (t) => {
  store(t, { accountId: "acct", trackSlug: ACTIVE_BP_TRACK_SLUG, currentScore: 6_200, currentTier: 4,
    completedTiers: tiers.slice(0, 4).map(x => x.TierGuid), claimedRewards: tiers.slice(0, 3).map(x => x.Rewards[0].RewardGuid) });
  const published: Array<[string, any]> = [];
  t.mock.method(redisClient, "publish", async (channel: string, message: string) => { published.push([channel, JSON.parse(message)]); return 1; });
  let response: any;
  const claimAll = () => handleSsc_invoke_claim_all_milestone_reward_track_tiers(
    { token: { id: "acct" }, body: { TrackSlug: ACTIVE_BP_TRACK_SLUG } } as any,
    { send: (body: any) => { response = body; } } as any,
  );
  await claimAll();
  assert.equal(response.body.RewardTrackStates[0].ClaimedRewards.length, 4);
  const [granted, update] = published;
  assert.equal(granted[0], "rewards:granted");
  assert.equal(granted[1].rewards[0].InventoryHsda, tiers[3].Rewards[0].InventoryHsda);
  assert.equal(update[0], "reward_track:updated");
  assert.equal(update[1].updateContext, 1);
  assert.equal(update[1].guid, "e693b965-7c8c-40f7-b89f-14c79dd6a609");
  assert.equal(update[1].claimedRewards.length, 4);
  // A second press claims nothing and pushes nothing.
  await claimAll();
  assert.equal(published.length, 2);
});
