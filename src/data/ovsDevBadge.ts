// The "OVS Dev" profile badge (2026-10-03): a StatTrackingBundleData in OVS_P, made like the game's
// EVO 2022 badges (no count shown, hidden from players who don't own it). Only the accounts in
// OVS_DEV_ACCOUNT_IDS own it (cosmeticEntitlementService); its catalog row is in dotnet/tools/assets/end-game-assets.json (sync_assets.mjs).
// The art and the Unreal script are in the Codex workspace:
// work/badges/make_ovs_dev_badge.py and scripts/ue/create_ovs_dev_badge.py.
import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

export const OVS_DEV_BADGE_SLUG = "stat_tracking_bundle_ovs_dev";
export const OVS_DEV_BADGE_ASSET_PATH = "/OVS/Rewards/Badges/OVSDev/stat_tracking_bundle_ovs_dev.stat_tracking_bundle_ovs_dev";
// The badge's ProfileDataField: the profile's stat_trackers value the game reads for it.
export const OVS_DEV_BADGE_PROFILE_FIELD = "OVSDev";

export const OVS_DEV_BADGE_INVENTORY: InvetoryKeysDefs = {
  [OVS_DEV_BADGE_SLUG]: {
    name: OVS_DEV_BADGE_SLUG,
    slug: OVS_DEV_BADGE_SLUG,
    type_class: "unlockable",
    max_count: 1,
    max_count_type: "strict",
    client_access: false,
    data: {
      AssetPath: OVS_DEV_BADGE_ASSET_PATH,
      EnabledForShipping: true,
      AssociatedCharacter: "Base",
      DisplayName: "OVS Dev",
      Rarity: "None",
      RewardThumbnail: "/OVS/Rewards/Badges/OVSDev/UI_Stat_OVSDev_Badge.UI_Stat_OVSDev_Badge",
      RewardThumbnailMaterial: "",
    },
    private_data: {},
    description: "",
    log_item_transactions: true,
    tags: ["stat_tracking_bundle", "unlockable"],
    type_options: {},
    seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
    propagate_to_owner: false,
    created_at: { _hydra_unix_date: 1791000000 },
    updated_at: { _hydra_unix_date: 1791000000 },
    id: createHash("sha1").update(`ovs-badge:${OVS_DEV_BADGE_SLUG}`).digest("hex").slice(0, 24),
  },
};
