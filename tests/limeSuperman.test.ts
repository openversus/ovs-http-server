import assert from "node:assert/strict";
import test from "node:test";
import { LIME_SUPERMAN_ASSET, LIME_SUPERMAN_INVENTORY, LIME_SUPERMAN_SLUG, LIME_SUPERMAN_PATH } from "../src/data/limeSuperman";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
import { filterOwnedByDefaultSlugs } from "../src/services/cosmeticEntitlementService";

test("Metallic Lime Superman is a separate, shipping-enabled Superman skin everyone owns", () => {
  assert.equal(LIME_SUPERMAN_ASSET.character_slug, "character_superman");
  assert.equal(LIME_SUPERMAN_ASSET.assetPath, LIME_SUPERMAN_PATH);
  assert.equal(INVENTORY_DEFINITIONS[LIME_SUPERMAN_SLUG], LIME_SUPERMAN_INVENTORY);
  assert.equal((LIME_SUPERMAN_INVENTORY.data as any).DisplayName, "Metallic Lime Superman");
  assert.equal((LIME_SUPERMAN_INVENTORY.data as any).EnabledForShipping, true);
  assert.equal(ENABLED_SKINS.character_superman.Slugs[0], "skin_superman_default");
  assert.equal(ENABLED_SKINS.character_superman.Slugs.filter(s => s === LIME_SUPERMAN_SLUG).length, 1);
  assert.ok(filterOwnedByDefaultSlugs([LIME_SUPERMAN_SLUG]).includes(LIME_SUPERMAN_SLUG));
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(x => x.id === LIME_SUPERMAN_INVENTORY.id).length, 1);
});
