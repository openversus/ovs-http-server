import "./ovsDevBadge.env";
import assert from "node:assert/strict";
import test from "node:test";
import { INVENTORY_DEFINITIONS } from "../src/data/inventoryDefs";
import { OVS_DEV_BADGE_SLUG } from "../src/data/ovsDevBadge";
import { filterOwnedByDefaultSlugs, isOvsDevAccount } from "../src/services/cosmeticEntitlementService";

test("the OVS Dev badge is a stat-tracking bundle in the catalog", () => {
  const def = (INVENTORY_DEFINITIONS as any)[OVS_DEV_BADGE_SLUG];
  assert.ok(def);
  assert.ok(def.tags.includes("stat_tracking_bundle"));
  assert.equal(def.data.DisplayName, "OVS Dev");
});

test("only OVS_DEV_ACCOUNT_IDS own the OVS Dev badge", () => {
  // Nobody owns it by default; filterInventoryForEntitlements keeps it exactly when isOvsDevAccount.
  assert.ok(!filterOwnedByDefaultSlugs([OVS_DEV_BADGE_SLUG, "skin_shaggy_default"]).includes(OVS_DEV_BADGE_SLUG));
  assert.equal(isOvsDevAccount("dev2"), true);
  assert.equal(isOvsDevAccount("someone"), false);
});
