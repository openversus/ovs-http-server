import assert from "node:assert/strict";
import test from "node:test";
import { INVENTORY_DEFINITIONS, type InventoryDefData } from "../src/data/inventoryDefs";
import {
  PINK_MOUNTAINEER_JASON_ASSET,
  PINK_MOUNTAINEER_JASON_INVENTORY,
  PINK_MOUNTAINEER_JASON_PATH,
  PINK_MOUNTAINEER_JASON_SLUG,
} from "../src/data/pinkMountaineerJason";
import { ENABLED_SKINS } from "../src/data/skins";

test("Pink Mountaineer Jason is a distinct S05-based Jason skin", () => {
  assert.equal(PINK_MOUNTAINEER_JASON_ASSET.assetPath, PINK_MOUNTAINEER_JASON_PATH);
  assert.equal(PINK_MOUNTAINEER_JASON_ASSET.character_slug, "character_Jason");
  assert.equal(INVENTORY_DEFINITIONS[PINK_MOUNTAINEER_JASON_SLUG], PINK_MOUNTAINEER_JASON_INVENTORY);
  assert.equal((PINK_MOUNTAINEER_JASON_INVENTORY.data as InventoryDefData).DisplayName,
    "Pink Abdominal Mountaineer Jason");
  assert.equal(ENABLED_SKINS.character_Jason.Slugs.filter(slug => slug === PINK_MOUNTAINEER_JASON_SLUG).length, 1);
  assert.equal(Object.values(INVENTORY_DEFINITIONS)
    .filter(value => value.id === PINK_MOUNTAINEER_JASON_INVENTORY.id).length, 1);
});
