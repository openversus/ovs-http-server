import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_BATCH8, CHROMIUM_BATCH8_ASSETS, CHROMIUM_BATCH8_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

const defaults: Record<string, string> = {
  character_C027: "skin_C027_default",
  character_C026: "skin_c026_default",
  character_c024: "skin_c024_default",
  character_C025: "skin_C025_default",
  character_C031: "skin_c031_s05",
  character_C029: "c029_default",
  character_c038: "C038",
};

const nativeSources: Record<string, string> = {
  ...defaults,
  character_C031: "skin_c031_s00",
};

test("Chromium final batch catalog, inventory and native portraits agree", () => {
  assert.equal(CHROMIUM_BATCH8.length, 7);
  const ids = new Set<string>();
  for (const [index, skin] of CHROMIUM_BATCH8.entries()) {
    const asset = CHROMIUM_BATCH8_ASSETS[index];
    const item = CHROMIUM_BATCH8_INVENTORY[skin.slug];
    const native = INVENTORY_DEFINITIONS[nativeSources[skin.characterSlug]].data as InventoryDefData;
    const data = item.data as InventoryDefData;
    assert.equal(asset.assetPath, `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`);
    assert.equal(asset.character_slug, skin.characterSlug);
    assert.equal(INVENTORY_DEFINITIONS[skin.slug], item);
    assert.equal(data.AssetPath, asset.assetPath);
    assert.equal(data.AssociatedCharacter, native.AssociatedCharacter);
    assert.equal(data.RewardThumbnail, native.RewardThumbnail);
    assert.equal(data.RewardThumbnailMaterial, native.RewardThumbnailMaterial);
    assert.ok(!item.tags.includes("unlock_location_battlepass"));
    assert.ok(!ids.has(item.id));
    ids.add(item.id);
  }
});

test("each final-batch skin has one exact owner and retains the existing first skin", () => {
  for (const skin of CHROMIUM_BATCH8) {
    const owners = Object.entries(ENABLED_SKINS)
      .filter(([, value]) => (value.Slugs as readonly string[]).includes(skin.slug))
      .map(([key]) => key);
    assert.deepEqual(owners, [skin.characterSlug]);
    const list = ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs;
    assert.equal(list[0], defaults[skin.characterSlug]);
    assert.equal(list.filter(value => value === skin.slug).length, 1);
  }
});

test("final-batch IDs and slugs are globally unique", () => {
  for (const skin of CHROMIUM_BATCH8) {
    const item = CHROMIUM_BATCH8_INVENTORY[skin.slug];
    assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(value => value.id === item.id).length, 1);
    assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(slug => slug === skin.slug).length, 1);
  }
});
