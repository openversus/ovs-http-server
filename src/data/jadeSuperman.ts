import type { InventoryDef } from "./inventoryDefs";
import { CHROMIUM_SHAGGY_INVENTORY } from "./chromiumSkins";

export const JADE_SUPERMAN_SLUG = "skin_ovs_jade_superman";
export const JADE_SUPERMAN_PATH =
  "/OVS/Rewards/Skins/Jade/Superman/Catalog/skin_ovs_jade_superman.skin_ovs_jade_superman";
export const JADE_SUPERMAN_ASSET = {
  slug: JADE_SUPERMAN_SLUG, assetType: "SkinData", assetPath: JADE_SUPERMAN_PATH,
  character_slug: "character_superman", enabled: true,
} as const;

// Standalone local-test variant; does not change Chromium or any reward track.
export const JADE_SUPERMAN_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1140",
  name: JADE_SUPERMAN_SLUG,
  slug: JADE_SUPERMAN_SLUG,
  data: {
    AssetPath: JADE_SUPERMAN_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "Superman",
    DisplayName: "Jade Superman",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    // Match the inherited, working Chromium portrait while this material trial is tested.
    RewardThumbnail: "/OVS/Rewards/Skins/Chromium/Superman/UI/T_Chromium_Superman_Portrait.T_Chromium_Superman_Portrait",
    RewardThumbnailMaterial: "/OVS/Rewards/Skins/Chromium/Superman/UI/MI_Chromium_Superman_Portrait.MI_Chromium_Superman_Portrait",
  },
  description: "Satin green jade with pale mineral veins; native Superman model and animations.",
  tags: ["character_superman", "universe_dc", "skin", "unlockable"],
};
