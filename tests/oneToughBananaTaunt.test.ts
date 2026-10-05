import assert from "node:assert/strict";
import test from "node:test";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { ONE_TOUGH_BANANA_CHARACTER, ONE_TOUGH_BANANA_SLUG } from "../src/data/oneToughBananaTaunt";
import { getAllTaunts, getTauntsByChar } from "../src/data/taunts";
import { filterOwnedByDefaultSlugs } from "../src/services/cosmeticEntitlementService";

test("One Tough Banana is a Banana Guard taunt in the catalog", () => {
  const def = (INVENTORY_DEFINITIONS as any)[ONE_TOUGH_BANANA_SLUG];
  assert.ok(def);
  assert.ok(def.tags.includes("taunt"));
  assert.equal(def.data.AssociatedCharacter, "C034");
  assert.equal(def.data.DisplayName, "One Tough Banana");
});

test("everyone owns One Tough Banana, under Banana Guard's slug as the game spells it", () => {
  assert.equal(ONE_TOUGH_BANANA_CHARACTER, "character_BananaGuard");
  assert.ok(getTauntsByChar("character_BananaGuard").includes(ONE_TOUGH_BANANA_SLUG));
  assert.ok(getAllTaunts().includes(ONE_TOUGH_BANANA_SLUG));
  assert.ok(filterOwnedByDefaultSlugs([ONE_TOUGH_BANANA_SLUG]).includes(ONE_TOUGH_BANANA_SLUG));
});
