import assert from "node:assert/strict";
import test from "node:test";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { ONE_TOUGH_BANANA_CHARACTER, ONE_TOUGH_BANANA_SLUG } from "../src/data/oneToughBananaTaunt";
import { getTauntsByChar } from "../src/data/taunts";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "../src/data/milestones";
import { filterOwnedByDefaultSlugs } from "../src/services/cosmeticEntitlementService";

test("One Tough Banana is a Banana Guard taunt in the catalog", () => {
  const def = (INVENTORY_DEFINITIONS as any)[ONE_TOUGH_BANANA_SLUG];
  assert.ok(def);
  assert.ok(def.tags.includes("taunt"));
  assert.equal(def.data.AssociatedCharacter, "C034");
  assert.equal(def.data.DisplayName, "One Tough Banana");
});

test("One Tough Banana is battle pass tier 3, under Banana Guard's slug as the game spells it, owned once claimed", () => {
  assert.equal(ONE_TOUGH_BANANA_CHARACTER, "character_BananaGuard");
  assert.ok(getTauntsByChar("character_BananaGuard").includes(ONE_TOUGH_BANANA_SLUG));
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS.indexOf(ONE_TOUGH_BANANA_SLUG), 2);
  assert.ok(!OVS_BATTLEPASS_REWARD_SLUGS.includes("emote_jack_o_lantern"));
  assert.ok(!filterOwnedByDefaultSlugs([ONE_TOUGH_BANANA_SLUG]).includes(ONE_TOUGH_BANANA_SLUG));
});
