import type { InventoryDef } from "./inventoryDefs";
import { TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY } from "./chromiumSkins";

// Local visual prototype; independently identified so Crown Circuit is intact.
export const PURE_FIRE_SHAGGY_SLUG = "skin_ovs_purefire_shaggy";
export const PURE_FIRE_SHAGGY_PATH =
  "/OVS/Rewards/Skins/Top25/PureFire/Shaggy/Catalog/skin_ovs_purefire_shaggy.skin_ovs_purefire_shaggy";
export const PURE_FIRE_SHAGGY_ASSET = {
  slug: PURE_FIRE_SHAGGY_SLUG,
  assetType: "SkinData",
  assetPath: PURE_FIRE_SHAGGY_PATH,
  character_slug: "character_shaggy",
  enabled: true,
} as const;
export const PURE_FIRE_SHAGGY_INVENTORY: InventoryDef = {
  ...TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1137",
  name: PURE_FIRE_SHAGGY_SLUG,
  slug: PURE_FIRE_SHAGGY_SLUG,
  data: {
    ...TOP25_CROWN_CIRCUIT_SHAGGY_INVENTORY.data,
    AssetPath: PURE_FIRE_SHAGGY_PATH,
    DisplayName: "Pure Fire Shaggy - Prototype",
  },
  description: "Animated fire prototype for Shaggy; tester-gated.",
};
