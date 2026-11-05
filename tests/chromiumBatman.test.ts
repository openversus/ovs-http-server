import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_BATMAN_ASSET, CHROMIUM_BATMAN_INVENTORY, CHROMIUM_BATMAN_SLUG } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

test("Chromium Batman has matching inventory and catalog paths", () => {
  const asset = CHROMIUM_BATMAN_ASSET;
  assert.equal(asset.assetType, "SkinData");
  assert.equal(asset.enabled, true);
  assert.equal(asset.assetPath, `/OVS/Rewards/Skins/Chromium/Batman/Catalog/${CHROMIUM_BATMAN_SLUG}.${CHROMIUM_BATMAN_SLUG}`);
  assert.equal(INVENTORY_DEFINITIONS[asset.slug], CHROMIUM_BATMAN_INVENTORY);
  const data = CHROMIUM_BATMAN_INVENTORY.data as InventoryDefData;
  assert.equal(data.AssetPath, asset.assetPath);
  assert.equal(data.AssociatedCharacter, "Batman");
  assert.equal(data.DisplayName, "Chromium Batman");
});
test("Chromium Batman is only assigned to Batman and leaves the default first", () => {
  const owners = Object.entries(ENABLED_SKINS).filter(([, data]) => (data.Slugs as readonly string[]).includes(CHROMIUM_BATMAN_SLUG)).map(([slug]) => slug);
  assert.deepEqual(owners, ["character_batman"]);
  assert.equal(ENABLED_SKINS.character_batman.Slugs[0], "skin_batman_default");
  assert.equal(ENABLED_SKINS.character_batman.Slugs.filter(s => s === CHROMIUM_BATMAN_SLUG).length, 1);
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(d => d.id === CHROMIUM_BATMAN_INVENTORY.id).length, 1);
  assert.ok(!CHROMIUM_BATMAN_INVENTORY.tags.includes("unlock_location_battlepass"));
});
test("Batman uses the existing native portrait until screenshot replacement", () => {
  const native = INVENTORY_DEFINITIONS.skin_batman_default.data as InventoryDefData;
  const chrome = CHROMIUM_BATMAN_INVENTORY.data as InventoryDefData;
  assert.equal(chrome.RewardThumbnail, native.RewardThumbnail);
  assert.equal(chrome.RewardThumbnailMaterial, native.RewardThumbnailMaterial);
});
