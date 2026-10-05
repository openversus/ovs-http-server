import assert from "node:assert/strict";
import test from "node:test";
import { DataAssetModel } from "../src/database/DataAssets";
import { getAllAssets, getAllSkinsByChar, loadAssets, withoutTestCharacters } from "../src/loadAssets";
import { TEST_CHARACTER_SLUGS } from "../src/data/testCharacters";

const catalog = [
  { slug: "character_shaggy", assetType: "CharacterData", character_slug: "" },
  { slug: "skin_shaggy_default", assetType: "SkinData", character_slug: "character_shaggy" },
  { slug: "character_Meeseeks", assetType: "CharacterData", character_slug: "" },
  { slug: "skin_meeseeks_default", assetType: "SkinData", character_slug: "character_meeseeks" },
  { slug: "character_C099", assetType: "CharacterData", character_slug: "" },
  { slug: "taunt_c099", assetType: "TauntData", character_slug: "character_C099" },
];

test("the 8 retail test characters and their assets are dropped by default", () => {
  assert.deepEqual(withoutTestCharacters(catalog, false).map(a => a.slug), ["character_shaggy", "skin_shaggy_default"]);
  assert.equal(TEST_CHARACTER_SLUGS.length, 8);
});

test("ENABLE_TEST_CHARACTERS=true keeps them", () => {
  assert.equal(withoutTestCharacters(catalog, true).length, catalog.length);
});

test("loadAssets feeds the filtered catalog to everything built from it", async (t) => {
  t.mock.method(DataAssetModel, "find", () => ({ lean: () => ({ exec: async () => catalog }) }) as any);
  await loadAssets();
  assert.deepEqual(getAllAssets().map(a => a.slug), ["character_shaggy", "skin_shaggy_default"]);
  assert.deepEqual(Object.keys(getAllSkinsByChar()), ["character_shaggy"]);
});
