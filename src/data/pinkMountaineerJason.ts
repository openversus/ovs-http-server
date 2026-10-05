import type { InventoryDef } from "./inventoryDefs";
import { CHROMIUM_SHAGGY_INVENTORY } from "./chromiumSkins";

export const PINK_MOUNTAINEER_JASON_SLUG = "skin_ovs_pink_abdominal_mountaineer_jason";
export const PINK_MOUNTAINEER_JASON_PATH =
  "/OVS/Rewards/Skins/PinkMountaineerJason/Catalog/skin_ovs_pink_abdominal_mountaineer_jason.skin_ovs_pink_abdominal_mountaineer_jason";

export const PINK_MOUNTAINEER_JASON_ASSET = {
  slug: PINK_MOUNTAINEER_JASON_SLUG,
  assetType: "SkinData",
  assetPath: PINK_MOUNTAINEER_JASON_PATH,
  character_slug: "character_Jason",
  enabled: true,
} as const;

// Local test skin based on the native C035 S05 Mountaineer variant. The
// native S05 reward thumbnail remains in place until an OVS capture is made.
export const PINK_MOUNTAINEER_JASON_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1138",
  name: PINK_MOUNTAINEER_JASON_SLUG,
  slug: PINK_MOUNTAINEER_JASON_SLUG,
  data: {
    AssetPath: PINK_MOUNTAINEER_JASON_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "C035",
    DisplayName: "Pink Abdominal Mountaineer Jason",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    RewardThumbnail: "/MvsSeason04/Character/C035/S05/T_C035_S05.T_C035_S05",
    RewardThumbnailMaterial: "/MvsSeason04/Character/C035/S05/MI_C035_S05_RewardThumbnailMaterial.MI_C035_S05_RewardThumbnailMaterial",
  },
  description: "Mountaineer Jason with the pink abdominal texture treatment.",
  tags: ["character_Jason", "unlock_location_battlepass", "universe_friday_the_13th", "skin", "unlockable"],
};
