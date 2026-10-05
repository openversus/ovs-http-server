import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_PAIR, CHROMIUM_PAIR_ASSETS, CHROMIUM_PAIR_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

for (const [index, skin] of CHROMIUM_PAIR.entries()) {
  test(`${skin.displayName} catalog and character assignment`, () => {
    const asset = CHROMIUM_PAIR_ASSETS[index];
    assert.equal(asset.character_slug, skin.characterSlug);
    assert.equal(asset.assetType, "SkinData");
    assert.equal(asset.enabled, true);
    assert.equal(asset.assetPath, `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`);
    assert.equal(INVENTORY_DEFINITIONS[skin.slug], CHROMIUM_PAIR_INVENTORY[skin.slug]);
    assert.equal((INVENTORY_DEFINITIONS[skin.slug].data as InventoryDefData).AssetPath, asset.assetPath);
    const owners = Object.entries(ENABLED_SKINS).filter(([,data]) => (data.Slugs as readonly string[]).includes(skin.slug)).map(([name]) => name);
    assert.deepEqual(owners, [skin.characterSlug]);
    assert.match(ENABLED_SKINS[skin.characterSlug].Slugs[0], /_default$/);
  });
}
test("Chromium pair has distinct inventory IDs and does not claim battle-pass placement", () => {
  assert.equal(new Set(CHROMIUM_PAIR.map(s => s.id)).size, 2);
  for (const skin of CHROMIUM_PAIR) {
    assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(d => d.id === skin.id).length, 1);
    assert.ok(!CHROMIUM_PAIR_INVENTORY[skin.slug].tags.includes("unlock_location_battlepass"));
  }
});
