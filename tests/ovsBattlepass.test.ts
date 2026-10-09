import { TWERK_IT_OUT_SLUG } from "../src/data/twerkItOutTaunt";
import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import ts from "typescript";
import vm from "node:vm";
import { MILESTONE_REWARDS } from "../src/data/milestones";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { OVS_BATTLEPASS_INVENTORY } from "../src/data/ovsBattlepassInventory";
import { CHROMIUM_SHAGGY_ASSET, CHROMIUM_SKIN_SLUG_SET } from "../src/data/chromiumSkins";
import { DataAssetModel } from "../src/database/DataAssets";
import { loadAssets } from "../src/loadAssets";
import { unlockAllCharacters } from "../src/data/characters";
import { generate_hiss } from "../src/handlers/hiss_amalgation_get";
import { formatBattlepassState, getActiveBattlepassState } from "../src/data/battlepassState";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { OVS_BATTLEPASS_REWARD_SLUGS, OVS_BATTLEPASS_XP_PER_TIER } from "../src/data/milestones";
import { PINK_MOUNTAINEER_JASON_SLUG } from "../src/data/pinkMountaineerJason";
import { RECOVERED_EMOTE_SLUGS } from "../src/data/recoveredEmotes";
import { UNRELEASED_COSMETICS } from "../src/data/unreleasedCosmetics";
import { STATIC_TRACK_STATES_AFTER_BATTLEPASS, STATIC_TRACK_STATES_BEFORE_BATTLEPASS } from "../src/data/rewardTrackStates";

const track = MILESTONE_REWARDS.mrt_battlepass_season_five;
const community = ["emote_67_hands", "emote_ovs_pleading_cat", "emote_ovs_rickflick",
  "profileicon_ovs_duck_season", "profileicon_ovs_icy_glare", "profileicon_ovs_batmobile", "profileicon_ovs_jason",
  "profileicon_ovs_multiversus_tattoo"];
const featuredSkins = [PINK_MOUNTAINEER_JASON_SLUG, "skin_ovs_omniman_superman", "skin_ovs_shirtless_tattoo_shaggy"];

test("seasonal rewards form one diversified pass without Fighter Pass Chromium", () => {
  assert.deepEqual(track.data.Tiers.map(t => "InventoryHsda" in t.Rewards[0] ? t.Rewards[0].InventoryHsda : undefined),
    OVS_BATTLEPASS_REWARD_SLUGS);
  const tiers = track.data.Tiers;
  assert.equal(new Set(tiers.map(t => t.TierGuid)).size, tiers.length);
  assert.equal(new Set(tiers.flatMap(t => t.Rewards.map(r => r.RewardGuid))).size,
    tiers.reduce((n, t) => n + t.Rewards.length, 0));
  assert.ok(tiers.every((t, i) => i === 0 || t.ScoreThreshold > tiers[i - 1].ScoreThreshold));
  // The order is a fixed shuffle; every featured reward, recovered emote and unreleased cosmetic is in it once.
  assert.equal(new Set(OVS_BATTLEPASS_REWARD_SLUGS).size, OVS_BATTLEPASS_REWARD_SLUGS.length);
  const unreleased = UNRELEASED_COSMETICS.map(item => item.slug);
  for (const slug of [...community, ...featuredSkins, ...RECOVERED_EMOTE_SLUGS, ...unreleased]) assert.ok(OVS_BATTLEPASS_REWARD_SLUGS.includes(slug), slug);
  assert.ok(!OVS_BATTLEPASS_REWARD_SLUGS.includes("skin_ovs_knights_must_fall_bugs"));
  assert.deepEqual(tiers.map(t => t.ScoreThreshold), tiers.map((_, i) => i * OVS_BATTLEPASS_XP_PER_TIER));
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS.length, 51);
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS[0], "profileicon_ovs_multiversus_tattoo");
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS.at(-1), TWERK_IT_OUT_SLUG);
  // The last tier is a claimable reward, not the game's recurring infinite tier.
  assert.equal((MILESTONE_REWARDS as any).mrt_battlepass_season_five.data.bDoesLastTierRecurInfinitely, false);
  assert.ok(OVS_BATTLEPASS_REWARD_SLUGS.every(slug => !CHROMIUM_SKIN_SLUG_SET.has(slug)));
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS.filter(slug => community.includes(slug)).length, community.length);
  for (const slug of OVS_BATTLEPASS_REWARD_SLUGS) assert.ok(INVENTORY_DEFINITIONS[slug], slug);
});

test("battlepass item definitions have unique ids and real OVS paths", () => {
  assert.deepEqual(Object.keys(OVS_BATTLEPASS_INVENTORY), [
    "emote_67_hands", "emote_ovs_pleading_cat", "emote_ovs_rickflick",
    "profileicon_ovs_duck_season", "profileicon_ovs_icy_glare", "profileicon_ovs_batmobile", "profileicon_ovs_jason",
    "profileicon_ovs_multiversus_tattoo", "skin_ovs_omniman_superman", "skin_ovs_shirtless_tattoo_shaggy",
  ]);
  assert.equal(new Set(Object.values(OVS_BATTLEPASS_INVENTORY).map(d => d.id)).size,
    Object.keys(OVS_BATTLEPASS_INVENTORY).length);
  for (const [slug, def] of Object.entries(OVS_BATTLEPASS_INVENTORY)) {
    assert.equal(def.slug, slug);
    assert.equal(INVENTORY_DEFINITIONS[slug], def);
    assert.match((def.data as any).AssetPath, /^\/OVS\/Rewards\//);
  }
});

test("HISS visibility, definitions and owned inventory expose Chromium with metadata", async (t) => {
  const assets = [
    { slug: "character_shaggy", assetType: "CharacterData", enabled: true },
    CHROMIUM_SHAGGY_ASSET,
    { slug: "unknown_existing_asset", assetType: "SkinData", character_slug: "character_shaggy", enabled: true },
  ];
  t.mock.method(DataAssetModel, "find", () => ({ lean: () => ({ exec: async () => assets }) }) as any);
  await loadAssets();
  const hiss = generate_hiss().body.Data;
  // The hiss sends no item definitions, as on the dotnet branch (2026-10-04: to be added back only if a
  // cosmetic turns out to need them).
  assert.deepEqual(hiss["inventory-item-definitions"]._hydra_compressed, {});
  const enabled = hiss["enabled-assets-data"]._hydra_compressed as any;
  const config = enabled.ClientAssetData;
  assert.ok(config, "enabled-assets config exists");
  assert.ok(config.DefaultVisibleAssets.SkinSlugsByCharacter.character_shaggy.Slugs.includes(CHROMIUM_SHAGGY_ASSET.slug));
  assert.ok(!config.OwnedByDefaultInventoryItems.includes(CHROMIUM_SHAGGY_ASSET.slug));
  const inventory = unlockAllCharacters("test-account");
  const skin = inventory.find(i => i.item_slug === CHROMIUM_SHAGGY_ASSET.slug)!;
  assert.equal(skin.count, 1);
  assert.equal(skin.account_id, "test-account");
  assert.equal((skin.data as any).AssetPath, CHROMIUM_SHAGGY_ASSET.assetPath);
  assert.deepEqual(inventory.find(i => i.item_slug === "unknown_existing_asset")?.data, {});
});

// Evaluate only the static handler, avoiding any Redis/network side effects.
async function rewardFixture(source: string) {
  const ast = ts.createSourceFile("ssc.ts", source, ts.ScriptTarget.Latest, true);
  const fn = ast.statements.find(s => ts.isFunctionDeclaration(s)
    && s.name?.text === "handleSsc_invoke_get_milestone_reward_tracks")!;
  const context = {
    exports: {} as any,
    getActiveBattlepassState: async () => formatBattlepassState(),
    getCharacterMasteryStates: async () => [],
    AuthUtils: { DecodeClientToken: () => ({ id: "test-account" }) },
    STATIC_TRACK_STATES_BEFORE_BATTLEPASS,
    STATIC_TRACK_STATES_AFTER_BATTLEPASS,
  };
  vm.runInNewContext(ts.transpileModule(fn.getText(ast),
    { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, context);
  let result: any;
  await context.exports.handleSsc_invoke_get_milestone_reward_tracks({ token: { id: "test-account" } }, { send: (v: any) => { result = v; } });
  return JSON.parse(JSON.stringify(result.body.RewardTrackStates));
}

test("active pass appears once and starts at zero without fabricated claims", async () => {
  const current = await rewardFixture(readFileSync("src/handlers/ssc.ts", "utf8"));
  assert.equal(current.filter((s: any) => s.TrackSlug === track.slug).length, 1);
  for (const other of current.filter((s: any) => s.TrackSlug !== track.slug)) {
    assert.ok(!other.CompletedTiers.includes(track.data.Tiers[5].TierGuid));
    assert.ok(!other.ClaimedRewards.includes(track.data.Tiers[5].Rewards[0].RewardGuid));
  }
  const state = current.find((s: any) => s.TrackSlug === track.slug);
  assert.equal(state.CurrentScore, 0);
  assert.equal(state.CurrentTier, 0);
  assert.deepEqual(state.CompletedTiers, []);
  assert.deepEqual(state.ClaimedRewards, []);
});

test("saved per-account battlepass state is read, not reset or borrowed", async (t) => {
  const saved = { currentScore: 2000, currentTier: 1, completedTiers: ["earned-tier"], claimedRewards: ["claimed-reward"] };
  t.mock.method(PlayerRewardTrackStateModel, "findOne", (query: any) => {
    assert.deepEqual(query, { accountId: "account-a", trackSlug: track.slug });
    return { lean: () => ({ exec: async () => saved }) } as any;
  });
  const state = await getActiveBattlepassState("account-a");
  assert.deepEqual(state, formatBattlepassState(saved));
  // 2000 XP = tiers 1-2 under the current layout, whatever the stored arrays said.
  assert.equal(state.CurrentScore, 2000);
  assert.deepEqual(state.CompletedTiers, track.data.Tiers.slice(0, 2).map(t => t.TierGuid));
  // Battle-pass claims are the player's own, so the saved list is returned as-is.
  assert.deepEqual(state.ClaimedRewards, ["claimed-reward"]);
  assert.equal((await getActiveBattlepassState()).CurrentTier, 0);
});

test("skin primary asset names match wire slugs exactly", () => {
  for (const slug of ["skin_ovs_omniman_superman", "skin_ovs_chromium_shaggy", PINK_MOUNTAINEER_JASON_SLUG]) {
    assert.equal((INVENTORY_DEFINITIONS[slug].data as any).AssetPath.split(".").pop(), slug);
  }
});
