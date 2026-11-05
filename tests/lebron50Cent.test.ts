import assert from "node:assert/strict";
import test from "node:test";
import { LEBRON_50CENT_ASSET, LEBRON_50CENT_INVENTORY, LEBRON_50CENT_SLUG, LEBRON_50CENT_PATH } from "../src/data/lebron50Cent";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "../src/data/milestones";
import { filterOwnedByDefaultSlugs } from "../src/services/cosmeticEntitlementService";

test("Get Rich or Dunk Tryin' is a LeBron skin at battle pass tier 49, owned only once claimed", () => {
  assert.equal(LEBRON_50CENT_ASSET.character_slug, "character_c16");
  assert.equal(LEBRON_50CENT_ASSET.assetPath, LEBRON_50CENT_PATH);
  assert.equal(INVENTORY_DEFINITIONS[LEBRON_50CENT_SLUG], LEBRON_50CENT_INVENTORY);
  assert.equal((LEBRON_50CENT_INVENTORY.data as any).DisplayName, "Get Rich or Dunk Tryin'");
  assert.equal(ENABLED_SKINS.character_c16.Slugs.filter(s => s === LEBRON_50CENT_SLUG).length, 1);
  assert.equal(OVS_BATTLEPASS_REWARD_SLUGS.indexOf(LEBRON_50CENT_SLUG), 48);
  assert.ok(!filterOwnedByDefaultSlugs([LEBRON_50CENT_SLUG]).includes(LEBRON_50CENT_SLUG));
  assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(x => x.id === LEBRON_50CENT_INVENTORY.id).length, 1);
});
