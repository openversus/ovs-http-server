import { DataAsset, DataAssetModel } from "./database/DataAssets";
import { isTestCharacter } from "./data/testCharacters";
import env from "./env/env";

export type AssetType =
  | "AnnouncerPackData"
  | "BannerData"
  | "CharacterData"
  | "EmoteData"
  | "MvsGemHsda"
  | "MvsPerkHsda"
  | "ProfileIconData"
  | "RingOutVfxData"
  | "SkinData"
  | "StatTrackingBundleData"
  | "TauntData";

let ALL_ASSETS: DataAsset[] = [];

interface SkinByChar {
  [key: string]: SkinSlugs;
}

interface SkinSlugs {
  Slugs: string[];
}

export function getAllAssets() {
  return ALL_ASSETS;
}

/**
 * Drop the retail client's test characters and everything that belongs to them
 * (skins, taunts, ...) unless ENABLE_TEST_CHARACTERS is on. Everything the game
 * is told about characters (HISS, inventory, unlocks) is built from this list.
 */
export function withoutTestCharacters<T extends Pick<DataAsset, "slug" | "assetType" | "character_slug">>(
  assets: T[],
  enableTestCharacters: boolean = env.ENABLE_TEST_CHARACTERS,
): T[] {
  if (enableTestCharacters) return assets;
  return assets.filter((asset) =>
    !(asset.assetType === "CharacterData" && isTestCharacter(asset.slug))
    && !isTestCharacter(asset.character_slug));
}

export async function loadAssets() {
  ALL_ASSETS = withoutTestCharacters(await DataAssetModel.find({ enabled: true }).lean().exec());
}

export async function loadAssetsByType(assetType: AssetType) {
  return withoutTestCharacters(await DataAssetModel.find({ assetType, enabled: true }).lean().exec());
}

export function getAssetsByType(assetType: AssetType) {
  return ALL_ASSETS.filter((a) => a.assetType === assetType);
}

export function getAllSkinsByChar() {
  const chars = getAssetsByType("CharacterData");
  const skins = getAssetsByType("SkinData");
  const skinsByChar: SkinByChar = {};
  for (const char of chars) {
    skinsByChar[char.slug] = { Slugs: skins.filter((s) => s.character_slug === char.slug).map((s) => s.slug) };
  }
  return skinsByChar;
}

export function getAllTauntsByChar() {
  const chars = getAssetsByType("CharacterData");
  const skins = getAssetsByType("TauntData");
  const skinsByChar: SkinByChar = {};
  for (const char of chars) {
    skinsByChar[char.slug] = { Slugs: skins.filter((s) => s.character_slug === char.slug).map((s) => s.slug) };
  }
  return skinsByChar;
}
