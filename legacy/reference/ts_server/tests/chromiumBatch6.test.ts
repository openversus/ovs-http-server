import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_BATCH6, CHROMIUM_BATCH6_ASSETS, CHROMIUM_BATCH6_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

const defaults: Record<string, string> = {
  character_C023A: "skin_c023A_default", character_C023B: "skin_c023b_default",
  character_C017: "skin_iron_giant_default", character_taz: "skin_taz_default",
  character_C018: "skin_c018_default",
};

test("Chromium batch six catalog, inventory and native portraits agree", () => {
  assert.equal(CHROMIUM_BATCH6.length, 5);
  const ids = new Set<string>();
  for (const [index, skin] of CHROMIUM_BATCH6.entries()) {
    const asset = CHROMIUM_BATCH6_ASSETS[index];
    const item = CHROMIUM_BATCH6_INVENTORY[skin.slug];
    const native = INVENTORY_DEFINITIONS[defaults[skin.characterSlug]].data as InventoryDefData;
    const data = item.data as InventoryDefData;
    assert.equal(asset.assetPath, `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`);
    assert.equal(asset.character_slug, skin.characterSlug);
    assert.equal(INVENTORY_DEFINITIONS[skin.slug], item);
    assert.equal(data.AssetPath, asset.assetPath);
    assert.equal(data.AssociatedCharacter, native.AssociatedCharacter);
    assert.equal(data.RewardThumbnail, native.RewardThumbnail);
    assert.equal(data.RewardThumbnailMaterial, native.RewardThumbnailMaterial);
    assert.ok(!item.tags.includes("unlock_location_battlepass"));
    assert.ok(!ids.has(item.id)); ids.add(item.id);
  }
});

test("each batch-six skin has one exact owner and retains the existing first skin", () => {
  const priorFirst: Record<string, string> = {
    character_C023A: "skin_c023A_default", character_C023B: "skin_c023b_default",
    character_C017: "skin_c017_s01", character_taz: "skin_taz_default", character_C018: "skin_c018_default",
  };
  for (const skin of CHROMIUM_BATCH6) {
    const owners = Object.entries(ENABLED_SKINS).filter(([, value]) => (value.Slugs as readonly string[]).includes(skin.slug)).map(([key]) => key);
    assert.deepEqual(owners, [skin.characterSlug]);
    const list = ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs;
    assert.equal(list[0], priorFirst[skin.characterSlug]);
    assert.equal(list.filter(value => value === skin.slug).length, 1);
  }
});

test("batch-six IDs and slugs are globally unique", () => {
  for (const skin of CHROMIUM_BATCH6) {
    const item = CHROMIUM_BATCH6_INVENTORY[skin.slug];
    assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(value => value.id === item.id).length, 1);
    assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(slug => slug === skin.slug).length, 1);
  }
});
