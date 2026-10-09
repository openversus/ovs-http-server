import type { InventoryDef } from "./inventoryDefs";
import { CHROMIUM_SHAGGY_INVENTORY } from "./chromiumSkins";

export const LEBRON_50CENT_SLUG = "skin_ovs_lebron_50cent";
export const LEBRON_50CENT_PATH =
  "/OVS/Rewards/Skins/LeBron50Cent/Catalog/skin_ovs_lebron_50cent.skin_ovs_lebron_50cent";
export const LEBRON_50CENT_ASSET = {
  slug: LEBRON_50CENT_SLUG, assetType: "SkinData", assetPath: LEBRON_50CENT_PATH,
  character_slug: "character_c16", enabled: true,
} as const;

// Get Rich or Dunk Tryin': LeBron as 50 Cent (cap, beard, tattoos, grey tank), made by Glassconsumer69. Its own head
// part (the cap) on LeBron's skeleton, LeBron's body with a new colour map. A battle pass reward (tier 49, Tuggernuts
// 2026-10-08), so owned once claimed.
export const LEBRON_50CENT_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c113a",
  name: LEBRON_50CENT_SLUG,
  slug: LEBRON_50CENT_SLUG,
  data: {
    AssetPath: LEBRON_50CENT_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "C016",
    DisplayName: "Get Rich or Dunk Tryin'",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    // The skin asset carries the thumbnail the game shows (the server sends no item definitions).
    RewardThumbnail: "/OVS/Rewards/Skins/LeBron50Cent/Thumbnail/T_OVS_LeBron50Cent_Thumb.T_OVS_LeBron50Cent_Thumb",
    RewardThumbnailMaterial: "/OVS/Rewards/Skins/LeBron50Cent/Thumbnail/MI_OVS_LeBron50Cent_Thumb.MI_OVS_LeBron50Cent_Thumb",
  },
  description: "LeBron as 50 Cent. Creator: Glassconsumer69.",
  tags: ["character_c16", "unlock_location_battlepass", "universe_space_jam", "skin", "unlockable"],
};
