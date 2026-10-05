import assert from "node:assert/strict";
import test from "node:test";
import { DataAssetModel } from "../src/database/DataAssets";
import { getAllAssets, getAllSkinsByChar, loadAssets, withoutTestCharacters } from "../src/loadAssets";
import { TEST_CHARACTER_SLUGS } from "../src/data/testCharacters";
import { UNRELEASED_COSMETICS } from "../src/data/unreleasedCosmetics";
import { OVS_DEV_BADGE_SLUG } from "../src/data/ovsDevBadge";
import { ONE_TOUGH_BANANA_SLUG } from "../src/data/oneToughBananaTaunt";

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
  // The unreleased cosmetics, the OVS Dev badge and One Tough Banana are served from code after the
  // database rows.
  const fromCode = new Set([...UNRELEASED_COSMETICS.map(item => item.slug), OVS_DEV_BADGE_SLUG, ONE_TOUGH_BANANA_SLUG]);
  assert.deepEqual(getAllAssets().map(a => a.slug).filter(slug => !fromCode.has(slug)), ["character_shaggy", "skin_shaggy_default"]);
  assert.equal(getAllAssets().filter(a => fromCode.has(a.slug)).length, fromCode.size);
  assert.deepEqual(Object.keys(getAllSkinsByChar()), ["character_shaggy"]);
});
