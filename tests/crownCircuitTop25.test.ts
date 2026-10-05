import assert from "node:assert/strict";
import test from "node:test";
import {
  TOP25_CROWN_CIRCUIT_SHAGGY_ASSET,
  TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY,
  TOP25_CROWN_CIRCUIT_SHAGGY_PATH,
  TOP25_CROWN_CIRCUIT_SHAGGY_SLUG,
} from "../src/data/chromiumSkins";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
import {
  filterInventoryForEntitlements,
  filterOwnedByDefaultSlugs,
  hasCrownCircuitEntitlement,
} from "../src/services/cosmeticEntitlementService";

const localTester = "69fe63059f6a20d3187bfc6c";

test("Crown Circuit catalog, inventory and Shaggy projection agree", () => {
  assert.equal(TOP25_CROWN_CIRCUIT_SHAGGY_ASSET.assetPath, TOP25_CROWN_CIRCUIT_SHAGGY_PATH);
  assert.equal(TOP25_CROWN_CIRCUIT_SHAGGY_ASSET.character_slug, "character_shaggy");
  assert.equal(INVENTORY_DEFINITIONS[TOP25_CROWN_CIRCUIT_SHAGGY_SLUG], TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY);
  assert.equal((TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY.data as InventoryDefData).DisplayName,
    "Top 25 Crown Circuit Shaggy");
  assert.equal(ENABLED_SKINS.character_shaggy.Slugs.filter((slug) => slug === TOP25_CROWN_CIRCUIT_SHAGGY_SLUG).length, 1);
});

test("Crown Circuit is visible but never globally owned", () => {
  assert.deepEqual(
    filterOwnedByDefaultSlugs(["skin_shaggy_default", TOP25_CROWN_CIRCUIT_SHAGGY_SLUG]),
    ["skin_shaggy_default"],
  );
});

test("local tester receives Crown Circuit and an ordinary account does not", async (t) => {
  // No saved battle-pass / Fighter Pass progress: only the rank/tester gate is under test.
  t.mock.method(PlayerRewardTrackStateModel, "findOne", () => ({ lean: () => ({ exec: async () => null }) }) as any);
  t.mock.method(PlayerRewardTrackStateModel, "find", () => ({ lean: () => ({ exec: async () => [] }) }) as any);
  assert.equal(await hasCrownCircuitEntitlement(localTester), true);
  const inventory = [
    { item_slug: "skin_shaggy_default" },
    { item_slug: TOP25_CROWN_CIRCUIT_SHAGGY_SLUG },
  ];
  assert.equal((await filterInventoryForEntitlements(inventory, localTester)).length, 2);
  assert.deepEqual(
    await filterInventoryForEntitlements(inventory, "not-allowlisted"),
    [{ item_slug: "skin_shaggy_default" }],
  );
});
