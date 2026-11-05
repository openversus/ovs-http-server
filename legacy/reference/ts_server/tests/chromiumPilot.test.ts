import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_PILOT, CHROMIUM_PILOT_ASSETS, CHROMIUM_PILOT_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

const defaults: Record<string, string> = {
  character_wonder_woman: "skin_wonder_woman_default",
  character_harleyquinn: "skin_harley_default",
  character_finn: "skin_finn_default",
};

test("Chromium pilot catalog, inventory and native portraits agree", () => {
  assert.equal(CHROMIUM_PILOT.length, 3);
  const ids = new Set<string>();
  for (const [index, skin] of CHROMIUM_PILOT.entries()) {
    const asset = CHROMIUM_PILOT_ASSETS[index];
    const inventory = CHROMIUM_PILOT_INVENTORY[skin.slug];
    const native = INVENTORY_DEFINITIONS[defaults[skin.characterSlug]].data as InventoryDefData;
    const data = inventory.data as InventoryDefData;
    assert.equal(asset.slug, skin.slug);
    assert.equal(asset.character_slug, skin.characterSlug);
    assert.equal(asset.assetPath, `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`);
    assert.equal(INVENTORY_DEFINITIONS[skin.slug], inventory);
    assert.equal(data.AssetPath, asset.assetPath);
    assert.equal(data.AssociatedCharacter, skin.associatedCharacter);
    assert.equal(data.DisplayName, skin.displayName);
    assert.equal(data.RewardThumbnail, native.RewardThumbnail);
    assert.equal(data.RewardThumbnailMaterial, native.RewardThumbnailMaterial);
    assert.ok(!inventory.tags.includes("unlock_location_battlepass"));
    assert.ok(!ids.has(inventory.id));
    ids.add(inventory.id);
  }
});

test("each pilot skin belongs to exactly one character and leaves its default first", () => {
  for (const skin of CHROMIUM_PILOT) {
    const owners = Object.entries(ENABLED_SKINS)
      .filter(([, value]) => (value.Slugs as readonly string[]).includes(skin.slug))
      .map(([slug]) => slug);
    assert.deepEqual(owners, [skin.characterSlug]);
    assert.equal(ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs[0], defaults[skin.characterSlug]);
    assert.equal(ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs.filter(value => value === skin.slug).length, 1);
  }
});

test("pilot IDs and slugs do not collide with any existing inventory definition", () => {
  for (const skin of CHROMIUM_PILOT) {
    const inventory = CHROMIUM_PILOT_INVENTORY[skin.slug];
    assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(value => value.id === inventory.id).length, 1);
    assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(slug => slug === skin.slug).length, 1);
  }
});
