import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_SHAGGY_ASSET, CHROMIUM_SHAGGY_INVENTORY, CHROMIUM_SHAGGY_PATH, CHROMIUM_SHAGGY_SLUG } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

test("Chromium slug, SkinData path, loader metadata and inventory agree", () => {
  assert.equal(CHROMIUM_SHAGGY_SLUG, "skin_ovs_chromium_shaggy");
  assert.equal(CHROMIUM_SHAGGY_PATH, "/OVS/Rewards/Skins/Chromium/Shaggy/Catalog/skin_ovs_chromium_shaggy.skin_ovs_chromium_shaggy");
  assert.equal(CHROMIUM_SHAGGY_ASSET.character_slug, "character_shaggy");
  assert.equal(CHROMIUM_SHAGGY_ASSET.assetType, "SkinData");
  assert.equal(CHROMIUM_SHAGGY_ASSET.enabled, true);
  const def = INVENTORY_DEFINITIONS[CHROMIUM_SHAGGY_SLUG];
  assert.equal(def, CHROMIUM_SHAGGY_INVENTORY);
  assert.equal(def.slug, CHROMIUM_SHAGGY_SLUG);
  assert.equal((def.data as InventoryDefData).AssetPath, CHROMIUM_SHAGGY_PATH);
  assert.equal((def.data as InventoryDefData).AssociatedCharacter, "Shaggy");
});

test("Chromium belongs only to Shaggy, without replacing the default skin", () => {
  const occurrences = Object.entries(ENABLED_SKINS).flatMap(([character, data]) =>
    data.Slugs.filter((slug) => slug === CHROMIUM_SHAGGY_SLUG).map(() => character));
  assert.deepEqual(occurrences, ["character_shaggy"]);
  assert.equal(ENABLED_SKINS.character_shaggy.Slugs[0], "skin_shaggy_default");
});
