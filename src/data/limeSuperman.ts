import type { InventoryDef } from "./inventoryDefs";
import { CHROMIUM_SHAGGY_INVENTORY } from "./chromiumSkins";

export const LIME_SUPERMAN_SLUG = "skin_ovs_lime_superman";
export const LIME_SUPERMAN_PATH =
  "/OVS/Rewards/Skins/Lime/Superman/Catalog/skin_ovs_lime_superman.skin_ovs_lime_superman";
export const LIME_SUPERMAN_ASSET = {
  slug: LIME_SUPERMAN_SLUG, assetType: "SkinData", assetPath: LIME_SUPERMAN_PATH,
  character_slug: "character_superman", enabled: true,
} as const;

// Metallic Lime Superman: a material variant on Superman's own model (Tuggernuts kept it for End Game, 2026-10-07, with his
// own thumbnail). Owned by everyone; not a Fighter Pass or battle pass reward.
export const LIME_SUPERMAN_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1141",
  name: LIME_SUPERMAN_SLUG,
  slug: LIME_SUPERMAN_SLUG,
  data: {
    AssetPath: LIME_SUPERMAN_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "Superman",
    DisplayName: "Metallic Lime Superman",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    // The skin asset carries the thumbnail the game shows (the server sends no item definitions).
    RewardThumbnail: "/OVS/Rewards/Skins/Chromium/Superman/UI/T_Chromium_Superman_Portrait.T_Chromium_Superman_Portrait",
    RewardThumbnailMaterial: "/OVS/Rewards/Skins/Chromium/Superman/UI/MI_Chromium_Superman_Portrait.MI_Chromium_Superman_Portrait",
  },
  description: "Metallic lime green; native Superman model and animations.",
  tags: ["character_superman", "universe_dc", "skin", "unlockable"],
};
