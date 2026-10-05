import type { InventoryDef } from "./inventoryDefs";
import { CHROMIUM_SHAGGY_INVENTORY } from "./chromiumSkins";

export const GOTH_MORTY_SLUG = "skin_ovs_goth_morty";
export const GOTH_MORTY_PATH = "/OVS/Rewards/Skins/GothMorty/Catalog/skin_ovs_goth_morty.skin_ovs_goth_morty";
export const GOTH_MORTY_ASSETS = [{
  slug: GOTH_MORTY_SLUG, assetType: "SkinData", assetPath: GOTH_MORTY_PATH,
  character_slug: "character_c019", enabled: true,
}] as const;

// Local test skin; native Morty thumbnail is temporary. No battle-pass changes.
export const GOTH_MORTY_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1107", name: GOTH_MORTY_SLUG, slug: GOTH_MORTY_SLUG,
  data: {
    AssetPath: GOTH_MORTY_PATH, EnabledForShipping: true, AssociatedCharacter: "C019",
    DisplayName: "Gothic Morty", DisplayNameLocalizationKey: "", DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    RewardThumbnail: "/Game/Character/Captures/C019/C019_C019_S00.C019_C019_S00",
    RewardThumbnailMaterial: "/Game/Panda_Main/Characters/C019/Skins/MI_C019_S00_RewardThumbnailMaterial.MI_C019_S00_RewardThumbnailMaterial",
  },
  description: "Gothic Morty with a top hat, tailored suit and cane.",
  tags: ["character_c019", "unlock_location_shop", "universe_rick_and_morty", "skin", "unlockable"],
};
