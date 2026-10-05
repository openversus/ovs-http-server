import assert from "node:assert/strict";
import test from "node:test";
import { PURE_FIRE_SHAGGY_ASSET, PURE_FIRE_SHAGGY_INVENTORY, PURE_FIRE_SHAGGY_PATH, PURE_FIRE_SHAGGY_SLUG } from "../src/data/pureFireSkin";
import { TOP25_CROWN_CIRCUIT_SHAGGY_SLUG } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { filterInventoryForEntitlements, filterOwnedByDefaultSlugs } from "../src/services/cosmeticEntitlementService";

test("Pure Fire is a separate catalog/skin/inventory identity", () => {
  assert.notEqual(PURE_FIRE_SHAGGY_SLUG, TOP25_CROWN_CIRCUIT_SHAGGY_SLUG);
  assert.equal(PURE_FIRE_SHAGGY_ASSET.assetPath, PURE_FIRE_SHAGGY_PATH);
  assert.equal(PURE_FIRE_SHAGGY_ASSET.character_slug, "character_shaggy");
  assert.equal(INVENTORY_DEFINITIONS[PURE_FIRE_SHAGGY_SLUG], PURE_FIRE_SHAGGY_INVENTORY);
  assert.equal((PURE_FIRE_SHAGGY_INVENTORY.data as InventoryDefData).DisplayName, "Pure Fire Shaggy - Prototype");
  assert.equal(ENABLED_SKINS.character_shaggy.Slugs.filter(s => s === PURE_FIRE_SHAGGY_SLUG).length, 1);
  assert.equal(ENABLED_SKINS.character_shaggy.Slugs[0], "skin_shaggy_default");
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(v => v.id === PURE_FIRE_SHAGGY_INVENTORY.id).length, 1);
});
test("Pure Fire is never globally owned and uses the existing tester gate", async (t) => {
  // No saved battle-pass / Fighter Pass progress: only the rank/tester gate is under test.
  t.mock.method(PlayerRewardTrackStateModel, "findOne", () => ({ lean: () => ({ exec: async () => null }) }) as any);
  t.mock.method(PlayerRewardTrackStateModel, "find", () => ({ lean: () => ({ exec: async () => [] }) }) as any);
  const slugs = ["skin_shaggy_default", PURE_FIRE_SHAGGY_SLUG, TOP25_CROWN_CIRCUIT_SHAGGY_SLUG];
  assert.deepEqual(filterOwnedByDefaultSlugs(slugs), ["skin_shaggy_default"]);
  const inventory = slugs.map(item_slug => ({ item_slug }));
  assert.deepEqual(await filterInventoryForEntitlements(inventory, "not-allowlisted"), [{ item_slug: "skin_shaggy_default" }]);
  assert.equal((await filterInventoryForEntitlements(inventory, "69fe63059f6a20d3187bfc6c")).length, 3);
});
