import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_BATCH7, CHROMIUM_BATCH7_ASSETS, CHROMIUM_BATCH7_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

const defaults: Record<string, string> = {
  character_tom_and_jerry: "skin_tom_and_jerry_default",
  character_C021: "skin_c021_default",
  character_C020: "skin_c020_default",
  character_c019: "skin_c019_default",
  character_c16: "skin_c016_default",
  character_bugs_bunny: "skin_bugs_bunny_default",
  character_Jason: "skin_jason_000",
  character_arya: "skin_arya_default",
  character_c036: "skin_c036",
  character_C030: "skin_c030_default",
};

const nativeSources = {
  ...defaults,
  character_Jason: "skin_c035_s02",
};

test("Chromium batch seven catalog, inventory and native portraits agree", () => {
  assert.equal(CHROMIUM_BATCH7.length, 10);
  const ids = new Set<string>();
  for (const [index, skin] of CHROMIUM_BATCH7.entries()) {
    const asset = CHROMIUM_BATCH7_ASSETS[index];
    const item = CHROMIUM_BATCH7_INVENTORY[skin.slug];
    const native = INVENTORY_DEFINITIONS[nativeSources[skin.characterSlug as keyof typeof nativeSources]].data as InventoryDefData;
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

test("each batch-seven skin has one exact owner and retains the existing first skin", () => {
  for (const skin of CHROMIUM_BATCH7) {
    const owners = Object.entries(ENABLED_SKINS)
      .filter(([, value]) => (value.Slugs as readonly string[]).includes(skin.slug))
      .map(([key]) => key);
    assert.deepEqual(owners, [skin.characterSlug]);
    const list = ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs;
    assert.equal(list[0], defaults[skin.characterSlug]);
    assert.equal(list.filter(value => value === skin.slug).length, 1);
  }
});

test("batch-seven IDs and slugs are globally unique", () => {
  for (const skin of CHROMIUM_BATCH7) {
    const item = CHROMIUM_BATCH7_INVENTORY[skin.slug];
    assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(value => value.id === item.id).length, 1);
    assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(slug => slug === skin.slug).length, 1);
  }
});
