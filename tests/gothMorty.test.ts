import assert from "node:assert/strict";
import test from "node:test";
import { GOTH_MORTY_ASSETS, GOTH_MORTY_SLUG as slug, GOTH_MORTY_INVENTORY as item } from "../src/data/gothMortySkin";
import { INVENTORY_DEFINITIONS as defs, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
test("Gothic Morty has one native-case owner and consistent inventory", () => {
  assert.equal(defs[slug], item);
  assert.deepEqual(Object.entries(ENABLED_SKINS).filter(([, v]) => (v.Slugs as readonly string[]).includes(slug)).map(([k]) => k), ["character_c019"]);
  assert.equal(ENABLED_SKINS.character_c019.Slugs[0], "skin_c019_default");
  const data=item.data as InventoryDefData;
  const native=defs.skin_c019_default.data as InventoryDefData;
  assert.equal(data.AssetPath,GOTH_MORTY_ASSETS[0].assetPath);
  assert.equal(data.AssociatedCharacter,native.AssociatedCharacter);
  assert.equal(data.RewardThumbnail,native.RewardThumbnail);
  assert.equal(data.RewardThumbnailMaterial,native.RewardThumbnailMaterial);
  assert.equal(Object.values(defs).filter(v => v.id===item.id).length,1);
  assert.ok(!item.tags.includes("unlock_location_battlepass"));
});
