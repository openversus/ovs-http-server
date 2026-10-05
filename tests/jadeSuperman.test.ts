import assert from "node:assert/strict";
import test from "node:test";
import { JADE_SUPERMAN_ASSET, JADE_SUPERMAN_INVENTORY, JADE_SUPERMAN_SLUG, JADE_SUPERMAN_PATH } from "../src/data/jadeSuperman";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";

test("Jade Superman is a separate, shipping-enabled native Superman variant", () => {
  assert.equal(JADE_SUPERMAN_ASSET.character_slug, "character_superman");
  assert.equal(JADE_SUPERMAN_ASSET.assetPath, JADE_SUPERMAN_PATH);
  assert.equal(INVENTORY_DEFINITIONS[JADE_SUPERMAN_SLUG], JADE_SUPERMAN_INVENTORY);
  assert.equal((JADE_SUPERMAN_INVENTORY.data as any).DisplayName, "Jade Superman");
  assert.equal((JADE_SUPERMAN_INVENTORY.data as any).EnabledForShipping, true);
  assert.equal(ENABLED_SKINS.character_superman.Slugs[0], "skin_superman_default");
  assert.equal(ENABLED_SKINS.character_superman.Slugs.filter(s => s === JADE_SUPERMAN_SLUG).length, 1);
  assert.ok(ENABLED_SKINS.character_superman.Slugs.includes("skin_ovs_chromium_superman"));
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(x => x.id === JADE_SUPERMAN_INVENTORY.id).length, 1);
});
