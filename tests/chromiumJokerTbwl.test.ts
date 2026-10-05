import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_JOKER_TBWL_ASSET, CHROMIUM_JOKER_TBWL_INVENTORY, CHROMIUM_JOKER_TBWL_PATH, CHROMIUM_JOKER_TBWL_SLUG } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

test("Chromium Joker uses the Batman Who Laughs native portrait and exact catalog path", () => {
  const native = INVENTORY_DEFINITIONS.skin_c028_s03.data as InventoryDefData;
  const data = CHROMIUM_JOKER_TBWL_INVENTORY.data as InventoryDefData;
  assert.equal(CHROMIUM_JOKER_TBWL_ASSET.assetPath, CHROMIUM_JOKER_TBWL_PATH);
  assert.equal(CHROMIUM_JOKER_TBWL_ASSET.character_slug, "character_C028");
  assert.equal(data.AssociatedCharacter, native.AssociatedCharacter);
  assert.equal(data.RewardThumbnail, native.RewardThumbnail);
  assert.equal(data.RewardThumbnailMaterial, native.RewardThumbnailMaterial);
  assert.equal(INVENTORY_DEFINITIONS[CHROMIUM_JOKER_TBWL_SLUG], CHROMIUM_JOKER_TBWL_INVENTORY);
  assert.ok(!CHROMIUM_JOKER_TBWL_INVENTORY.tags.includes("unlock_location_battlepass"));
});

test("Chromium Batman Who Laughs belongs only to Joker without replacing defaults", () => {
  const owners = Object.entries(ENABLED_SKINS).filter(([, value]) => (value.Slugs as readonly string[]).includes(CHROMIUM_JOKER_TBWL_SLUG)).map(([key]) => key);
  assert.deepEqual(owners, ["character_C028"]);
  assert.equal(ENABLED_SKINS.character_C028.Slugs[0], "skin_c028_default");
  assert.equal(ENABLED_SKINS.character_C028.Slugs.filter(value => value === CHROMIUM_JOKER_TBWL_SLUG).length, 1);
});

test("Chromium Joker inventory ID and slug are globally unique", () => {
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(value => value.id === CHROMIUM_JOKER_TBWL_INVENTORY.id).length, 1);
  assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(slug => slug === CHROMIUM_JOKER_TBWL_SLUG).length, 1);
});
