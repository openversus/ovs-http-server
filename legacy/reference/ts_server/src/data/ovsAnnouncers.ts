import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

// OpenVersus announcer packs, cooked into OVS_P under /OVS/Rewards/AnnouncerPacks. Their voice
// lines are Wwise banks shipped in a separate raw pak (see docs/ANNOUNCER_PACKS.md).
// Cyffer21's pack (2026-10-08) replaced the one-line test pack: ten lines he recorded, his own art.
export const OVS_ANNOUNCERS = [
  {
    slug: "announcer_pack_ovs_cyffer21",
    displayName: "Cyffer21",
    thumbnail: "/OVS/Rewards/AnnouncerPacks/T_OVS_Cyffer21_Announcer.T_OVS_Cyffer21_Announcer",
  },
] as const;

export const OVS_ANNOUNCER_SLUGS = OVS_ANNOUNCERS.map((pack) => pack.slug);

export const OVS_ANNOUNCER_INVENTORY: InvetoryKeysDefs = Object.fromEntries(
  OVS_ANNOUNCERS.map(({ slug, displayName, thumbnail }) => [slug, {
    name: slug,
    slug,
    type_class: "unlockable",
    max_count: 1,
    max_count_type: "strict",
    client_access: false,
    data: {
      AssetPath: `/OVS/Rewards/AnnouncerPacks/${slug}.${slug}`,
      EnabledForShipping: true,
      AssociatedCharacter: "Base",
      DisplayName: displayName,
      Rarity: "Epic",
      RewardThumbnail: thumbnail,
      RewardThumbnailMaterial: "",
    },
    private_data: null,
    description: "",
    log_item_transactions: true,
    tags: ["rarity_epic", "announcer_pack", "unlockable"],
    type_options: {},
    seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
    propagate_to_owner: false,
    created_at: { _hydra_unix_date: 1790640000 },
    updated_at: { _hydra_unix_date: 1790640000 },
    id: createHash("sha256").update(`ovs-announcer:${slug}`).digest("hex").slice(0, 24),
  }]),
);
