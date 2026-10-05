import { DataAsset, DataAssetModel } from "./database/DataAssets";
import { isTestCharacter } from "./data/testCharacters";
import env from "./env/env";
import { UNRELEASED_COSMETICS } from "./data/unreleasedCosmetics";
import { OVS_DEV_BADGE_ASSET_PATH, OVS_DEV_BADGE_SLUG } from "./data/ovsDevBadge";
import { ONE_TOUGH_BANANA_ASSET_PATH, ONE_TOUGH_BANANA_CHARACTER, ONE_TOUGH_BANANA_SLUG } from "./data/oneToughBananaTaunt";

export type AssetType =
  | "AnnouncerPackData"
  | "BannerData"
  | "CharacterData"
  | "EmoteData"
  | "MvsGemHsda"
  | "MvsMetaWorldBuffHsda"
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
  ALL_ASSETS = [...ALL_ASSETS, ...unreleasedCosmeticAssets(ALL_ASSETS), ...ovsDevBadgeAssets(ALL_ASSETS), ...oneToughBananaAssets(ALL_ASSETS)];
}

/**
 * The unreleased cosmetics (data/unreleasedCosmetics.ts) as served assets. Their item definitions
 * are in INVENTORY_DEFINITIONS; as battle-pass rewards they are owned once claimed. A slug the
 * database already serves is not added twice.
 */
function unreleasedCosmeticAssets(served: DataAsset[]): DataAsset[] {
  const servedSlugs = new Set(served.map(asset => asset.slug.toLowerCase()));
  return UNRELEASED_COSMETICS
    .filter(item => !servedSlugs.has(item.slug.toLowerCase()))
    .map(item => ({ slug: item.slug, assetType: item.assetType, assetPath: item.assetPath, character_slug: item.character_slug, enabled: true }) as DataAsset);
}

/** The OVS Dev badge (data/ovsDevBadge.ts) as a served asset, unless the database already serves it. */
function ovsDevBadgeAssets(served: DataAsset[]): DataAsset[] {
  if (served.some(asset => asset.slug === OVS_DEV_BADGE_SLUG)) return [];
  return [{ slug: OVS_DEV_BADGE_SLUG, assetType: "StatTrackingBundleData", assetPath: OVS_DEV_BADGE_ASSET_PATH, character_slug: "", enabled: true } as DataAsset];
}

/** The One Tough Banana taunt (data/oneToughBananaTaunt.ts) as a served asset, unless the database already serves it. */
function oneToughBananaAssets(served: DataAsset[]): DataAsset[] {
  if (served.some(asset => asset.slug === ONE_TOUGH_BANANA_SLUG)) return [];
  return [{ slug: ONE_TOUGH_BANANA_SLUG, assetType: "TauntData", assetPath: ONE_TOUGH_BANANA_ASSET_PATH, character_slug: ONE_TOUGH_BANANA_CHARACTER, enabled: true } as DataAsset];
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
