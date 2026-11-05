import assert from "node:assert/strict";
import test from "node:test";
import {
  CHROMIUM_FIGHTER_PASS,
  CHROMIUM_SKIN_SLUGS,
  getChromiumMasteryTrackForCharacter,
} from "../src/data/chromiumSkins";
import { MILESTONE_REWARDS, OVS_BATTLEPASS_REWARD_SLUGS, OVS_BATTLEPASS_XP_PER_TIER } from "../src/data/milestones";
import {
  deriveTierState,
  getCharacterMasteryStates,
  getCrossedTierGrants,
  getRankedSetXp,
  getOwnedFighterPassSlugs,
  getTiersForTrack,
  getTrackPushFields,
} from "../src/data/rewardTracks";
import { FIGHTER_PASS_AVERAGE_SET_XP, FIGHTER_PASS_TIERS, FIGHTER_PASS_TOAST_SLUG } from "../src/data/fighterPass";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { handleSsc_invoke_get_milestone_reward_tracks } from "../src/handlers/ssc";

test("every Chromium skin is the unique capstone of its matching Fighter Pass", () => {
  assert.equal(CHROMIUM_FIGHTER_PASS.length, CHROMIUM_SKIN_SLUGS.length);
  assert.equal(new Set(CHROMIUM_FIGHTER_PASS.map(entry => entry.trackSlug)).size, CHROMIUM_FIGHTER_PASS.length);
  assert.equal(new Set(CHROMIUM_FIGHTER_PASS.map(entry => entry.skinSlug)).size, CHROMIUM_FIGHTER_PASS.length);

  for (const entry of CHROMIUM_FIGHTER_PASS) {
    const track = (MILESTONE_REWARDS as Record<string, any>)[entry.trackSlug];
    assert.ok(track, entry.trackSlug);
    const finalTier = track.data.Tiers.at(-1);
    assert.equal(finalTier.Rewards.length, 1, entry.trackSlug);
    assert.equal(finalTier.Rewards[0].InventoryHsda, entry.skinSlug, entry.trackSlug);
    assert.equal(finalTier.Rewards[0].RewardGrantMethod, "DirectInventoryItem");
    assert.ok(INVENTORY_DEFINITIONS[entry.skinSlug].tags.includes("unlock_location_fighter_mastery"));
    assert.equal(getChromiumMasteryTrackForCharacter(entry.characterSlug), entry.trackSlug);
    assert.equal(getChromiumMasteryTrackForCharacter(entry.characterSlug.toUpperCase()), entry.trackSlug);

    const tiers = getTiersForTrack(entry.trackSlug)!;
    const beforeCapstone = deriveTierState(finalTier.ScoreThreshold - 1, tiers);
    const atCapstone = deriveTierState(finalTier.ScoreThreshold, tiers);
    assert.ok(!beforeCapstone.claimedRewards.includes(finalTier.Rewards[0].RewardGuid));
    assert.ok(atCapstone.claimedRewards.includes(finalTier.Rewards[0].RewardGuid));
  }
});

test("fresh Fighter Pass state is zeroed and persisted states hydrate in one query", async (t) => {
  const shaggy = CHROMIUM_FIGHTER_PASS[0];
  const persisted = {
    accountId: "account-a",
    trackSlug: shaggy.trackSlug,
    currentScore: 930,
    currentTier: 1,
    completedTiers: ["tier-a", "tier-b"],
    claimedRewards: ["reward-a"],
    bHasPremium: false,
  };
  t.mock.method(PlayerRewardTrackStateModel, "find", (query: any) => {
    assert.equal(query.accountId, "account-a");
    assert.deepEqual(query.trackSlug.$in, CHROMIUM_FIGHTER_PASS.map(entry => entry.trackSlug));
    return { lean: () => ({ exec: async () => [persisted] }) } as any;
  });

  const states = await getCharacterMasteryStates("account-a");
  assert.equal(states.length, CHROMIUM_FIGHTER_PASS.length);
  // Tier arrays are re-derived from the saved score, not echoed from storage.
  const derived = deriveTierState(930, getTiersForTrack(shaggy.trackSlug)!);
  assert.equal(derived.completedTiers.length, 2);
  assert.deepEqual(states.find(state => state.TrackSlug === shaggy.trackSlug), {
    TrackSlug: shaggy.trackSlug,
    RewardTrackClass: "MvsCharacterMasteryRewardTrackHsda",
    CurrentScore: 930,
    CurrentTier: derived.currentTier,
    CompletedTiers: derived.completedTiers,
    ClaimedRewards: derived.claimedRewards,
    bHasPremium: false,
    InfiniteTierThreshold: 15,
    HighestClaimedInifiniteTier: -1,
  });
  assert.equal(states.find(state => state.TrackSlug === CHROMIUM_FIGHTER_PASS[1].trackSlug)?.CurrentScore, 0);
});

test("Chromium entitlement appears only after its Fighter Pass capstone", async (t) => {
  const before = CHROMIUM_FIGHTER_PASS[0];
  const earned = CHROMIUM_FIGHTER_PASS[1];
  const beforeThreshold = getTiersForTrack(before.trackSlug)!.at(-1)!.ScoreThreshold;
  const earnedThreshold = getTiersForTrack(earned.trackSlug)!.at(-1)!.ScoreThreshold;
  t.mock.method(PlayerRewardTrackStateModel, "find", () => ({
    lean: () => ({ exec: async () => [
      { trackSlug: before.trackSlug, currentScore: beforeThreshold - 1 },
      { trackSlug: earned.trackSlug, currentScore: earnedThreshold },
    ] }),
  }) as any);

  assert.deepEqual([...await getOwnedFighterPassSlugs("account-a")], [earned.skinSlug]);
});

test("Chromium skins no longer occupy seasonal Battle Pass tiers", () => {
  for (const chromium of CHROMIUM_SKIN_SLUGS) {
    assert.ok(!OVS_BATTLEPASS_REWARD_SLUGS.includes(chromium), chromium);
  }
});

test("every Fighter Pass pays toasts, battle-pass XP on tiers 5, 10 and 15, Chromium at ~50 ranked sets", () => {
  for (const entry of CHROMIUM_FIGHTER_PASS) {
    const track = (MILESTONE_REWARDS as Record<string, any>)[entry.trackSlug];
    const tiers = track.data.Tiers;
    assert.equal(tiers.length, 16, entry.trackSlug);
    assert.deepEqual(track.data.RewardsForEveryTier, []);
    assert.equal(track.data.bDoesLastTierRecurInfinitely, false);
    assert.equal(new Set(tiers.map((t: any) => t.TierGuid)).size, tiers.length);
    assert.ok(tiers.every((t: any, i: number) => i === 0 || t.ScoreThreshold > tiers[i - 1].ScoreThreshold));
    tiers.forEach((tier: any, index: number) => {
      assert.equal(tier.Rewards.length, 1);
      const [reward] = tier.Rewards;
      if (index === 4 || index === 9 || index === 14) {
        assert.equal(reward.RewardGrantMethod, "RewardTableLookup", `${entry.trackSlug} tier ${index + 1}`);
        assert.match(reward.RewardHsda, /^reward_xp_battlepass_\d+$/);
      } else if (index === tiers.length - 1) {
        assert.equal(reward.InventoryHsda, entry.skinSlug);
      } else {
        assert.equal(reward.InventoryHsda, FIGHTER_PASS_TOAST_SLUG, `${entry.trackSlug} tier ${index + 1}`);
        assert.ok(reward.DirectInventoryItemCount >= 10);
      }
    });
  }
  const capstone = FIGHTER_PASS_TIERS.at(-1)!.threshold;
  assert.equal(capstone / FIGHTER_PASS_AVERAGE_SET_XP, 50);
  assert.equal(Math.ceil(capstone / getRankedSetXp(true)), 42);
  assert.equal(Math.ceil(capstone / getRankedSetXp(false)), 63);
  const rewardGuids = CHROMIUM_FIGHTER_PASS.flatMap(entry =>
    (MILESTONE_REWARDS as Record<string, any>)[entry.trackSlug].data.Tiers.flatMap((t: any) => t.Rewards.map((r: any) => r.RewardGuid)));
  assert.equal(new Set(rewardGuids).size, rewardGuids.length);
});

test("crossing tiers pays each tier once, and completed tiers are marked claimed", () => {
  const { trackSlug } = CHROMIUM_FIGHTER_PASS[0];
  assert.deepEqual(getCrossedTierGrants(trackSlug, 0, 600), { toasts: 20, battlepassXp: 0 });
  assert.deepEqual(getCrossedTierGrants(trackSlug, 600, 1000), { toasts: 15, battlepassXp: 0 });
  assert.deepEqual(getCrossedTierGrants(trackSlug, 1000, 1400), { toasts: 0, battlepassXp: 0 });
  assert.deepEqual(getCrossedTierGrants(trackSlug, 2800, 3200), { toasts: 0, battlepassXp: 600 });
  assert.deepEqual(getCrossedTierGrants(trackSlug, 10_200, 10_600), { toasts: 0, battlepassXp: 700 });
  assert.deepEqual(getCrossedTierGrants(trackSlug, 22_200, 22_600), { toasts: 0, battlepassXp: 700 });
  const all = getCrossedTierGrants(trackSlug, 0, 25_000);
  // All three together are exactly one battle-pass tier.
  assert.equal(all.battlepassXp, OVS_BATTLEPASS_XP_PER_TIER);
  assert.equal(all.toasts, FIGHTER_PASS_TIERS.reduce((n, t) => n + (t.reward.kind === "toasts" ? t.reward.count : 0), 0));

  const tiers = getTiersForTrack(trackSlug)!;
  const done = deriveTierState(7000, tiers);
  assert.equal(done.completedTiers.length, 8);
  assert.deepEqual(done.claimedRewards, tiers.slice(0, 8).map(t => t.Rewards[0].RewardGuid));
});

test("realtime pushes carry each track's own class and infinite-tier threshold", () => {
  const shaggy = getTrackPushFields(CHROMIUM_FIGHTER_PASS[0].trackSlug);
  assert.equal(shaggy.rewardTrackClass, "MvsCharacterMasteryRewardTrackHsda");
  assert.equal(shaggy.infiniteTierThreshold, 15);
  const bp = getTrackPushFields("mrt_battlepass_season_five");
  assert.equal(bp.rewardTrackClass, "MvsBattlepassRewardTrackHsda");
  assert.equal(bp.infiniteTierThreshold, OVS_BATTLEPASS_REWARD_SLUGS.length - 1);
});

test("every realtime push uses the Guid get_milestone_reward_tracks served for that track", async (t) => {
  t.mock.method(PlayerRewardTrackStateModel, "find", () => ({ lean: () => ({ exec: async () => [] }) }) as any);
  t.mock.method(PlayerRewardTrackStateModel, "findOne", () => ({ lean: () => ({ exec: async () => null }) }) as any);
  let sent: any;
  await handleSsc_invoke_get_milestone_reward_tracks(
    { token: { id: "account-a" } } as any,
    { send: (body: any) => { sent = body; } } as any,
  );
  const served = new Map<string, string>(sent.body.RewardTrackStates.map((s: any) => [s.TrackSlug, s.Guid]));
  const slugs = ["mrt_battlepass_season_five", ...CHROMIUM_FIGHTER_PASS.map(entry => entry.trackSlug)];
  for (const slug of slugs) {
    assert.ok(served.has(slug), `${slug} served`);
    assert.equal(getTrackPushFields(slug).guid, served.get(slug), slug);
  }
  // The two tracks from the report: Shaggy has a real Guid, Banana Guard an empty one.
  assert.equal(getTrackPushFields("mrt_mastery_shaggy").guid, "b474fc18-2a9d-4c1d-a430-fc65fa1584d3");
  assert.equal(getTrackPushFields("mrt_battlepass_season_five").guid, "e693b965-7c8c-40f7-b89f-14c79dd6a609");
});

test("CurrentTier is the tier being worked towards, so the match-end banner never goes negative", () => {
  const tiers = getTiersForTrack(CHROMIUM_FIGHTER_PASS[0].trackSlug)!;
  // The reported case: 1,800 XP. Next tier is tier 4 at 2,000 -> "200 For Tier 4".
  const state = deriveTierState(1_800, tiers);
  assert.equal(state.currentTier, 3);
  assert.equal(tiers[state.currentTier].ScoreThreshold - 1_800, 200);
  assert.equal(deriveTierState(0, tiers).currentTier, 0);
  for (let score = 0; score < 25_000; score += 100) {
    const s = deriveTierState(score, tiers);
    assert.ok(tiers[s.currentTier].ScoreThreshold > score, `score ${score}`);
  }
  // A finished pass stays on its last tier instead of indexing past the end.
  assert.equal(deriveTierState(99_999, tiers).currentTier, tiers.length - 1);
});
