// Cosmetics that ship in the game files but were never released, picked by Jacob after testing each
// one in game (2026-10-02; the review is in the Codex workspace, work/unreleased-audit/reviewed.txt).
// They are End Game battle-pass rewards (milestones.ts). Their catalog rows, with the asset paths below,
// are in dotnet/tools/assets/end-game-assets.json (sync_assets.mjs).
import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

export type UnreleasedCosmetic = { slug: string; assetType: string; assetPath: string; character_slug: string; displayName: string };

export const UNRELEASED_COSMETICS: UnreleasedCosmetic[] = [
  // Animated (PS_Unknown_Glow); the Uncommon one is the same banner, static.
  { slug: "banner_pve_unknown_rare", assetType: "BannerData", assetPath: "/Game/Panda_Main/PreMatch/Banners/PVE_Unknown/PVE_UnknownBanner_Rare.PVE_UnknownBanner_Rare", character_slug: "", displayName: "PVE Unknown" },
  { slug: "emote_arya_laugh", assetType: "EmoteData", assetPath: "/Game/Panda_Main/Characters/Arya/DataAssets/Emote_C006_Laugh.Emote_C006_Laugh", character_slug: "", displayName: "Arya - Laugh" },
  { slug: "emote_c016_mind", assetType: "EmoteData", assetPath: "/Game/Panda_Main/Characters/c016/DataAssets/Emote_C016_Mind.Emote_C016_Mind", character_slug: "", displayName: "Lebron - Mind" },
  { slug: "emote_reindog_hearts", assetType: "EmoteData", assetPath: "/Game/Panda_Main/Characters/Creature/DataAssets/Emote_Reindog_Hearts.Emote_Reindog_Hearts", character_slug: "", displayName: "Reindog - Hearts" },
  { slug: "emote_superman_smile", assetType: "EmoteData", assetPath: "/Game/Panda_Main/Characters/Superman/DataAssets/Emote_C003_Smile.Emote_C003_Smile", character_slug: "", displayName: "Superman - Smile" },
  { slug: "emote_taz_tongue", assetType: "EmoteData", assetPath: "/Game/Panda_Main/Characters/C015/DataAssets/Emote_C015_Tongue.Emote_C015_Tongue", character_slug: "", displayName: "Taz - Tongue" },
  // The released icon has the Mystery Machine's lights off.
  { slug: "HB_SD_ProfileIcon_IntoTheMysteryMachineGang", assetType: "ProfileIconData", assetPath: "/Eventually_Ships/Cosmetics/ProfileIcons/DataAsset/HB_SD_ProfileIcon_IntoTheMysteryMachineGang.HB_SD_ProfileIcon_IntoTheMysteryMachineGang", character_slug: "", displayName: "Into the Mystery Machine, Gang!" },
  { slug: "ring_out_vfx_pfg_arrival", assetType: "RingOutVfxData", assetPath: "/Game/Panda_Main/Blueprints/Rewards/RingOutVfx/ROV_PFGArrival.ROV_PFGArrival", character_slug: "", displayName: "PFG Arrival" },
  // The asset says "C027 S14"; OVS_UnreleasedNames_33_P renames it in game.
  { slug: "skin_C027_s14", assetType: "SkinData", assetPath: "/MvsSeason03/Character/C027/Skins/S14/C027_S14.C027_S14", character_slug: "character_C027", displayName: "Tooniverse Nubia" },
  // A sailor: cap, bell-bottoms. The asset says "C029 S05"; OVS_UnreleasedNames_33_P renames it in game.
  { slug: "skin_c029_s05", assetType: "SkinData", assetPath: "/Eventually_Ships/Character/C029/Skins/S05/C029_S05.C029_S05", character_slug: "character_C029", displayName: "Sailor Aquaman" },
  { slug: "skin_c030_s14", assetType: "SkinData", assetPath: "/MvsSeason03/Character/C030/Skins/S14/C030_S14.C030_S14", character_slug: "character_C030", displayName: "Tooniverse The Powerpuff Girls" },
  // Joker.
  { slug: "Taunt_C028_CardSpring", assetType: "TauntData", assetPath: "/Game/Panda_Main/Characters/C028/DataAssets/Taunts/Taunt_C028_CardSpring.Taunt_C028_CardSpring", character_slug: "character_C028", displayName: "Read ‘Em and Weep, Chump!" },
  // Stripe.
  { slug: "taunt_c023b_cackle", assetType: "TauntData", assetPath: "/Game/Panda_Main/Characters/C023B/DataAssets/Taunts/Taunt_C023B_Cackle.Taunt_C023B_Cackle", character_slug: "character_C023B", displayName: "Cackle" },
  { slug: "taunt_finn_dance2", assetType: "TauntData", assetPath: "/Game/Panda_Main/Characters/Finn/DataAssets/Taunts/Taunt_Finn_Dance2.Taunt_Finn_Dance2", character_slug: "character_finn", displayName: "Dance2" },
  // Beetlejuice's unreleased S02 (a painter), refit onto the current skeleton as an OVS skin: the
  // game only has it on the old, shorter body (Codex workspace work/beetlejuice).
  { slug: "skin_ovs_painter_beetlejuice", assetType: "SkinData", assetPath: "/OVS/Rewards/Skins/Beetlejuice/Painter/Catalog/skin_ovs_painter_beetlejuice.skin_ovs_painter_beetlejuice", character_slug: "character_c024", displayName: "Painter Beetlejuice" },
];

const ITEM_TAGS: Record<string, string> = {
  RingOutVfxData: "ring_out_vfx", EmoteData: "emote", TauntData: "taunt", SkinData: "skin",
  BannerData: "banner", ProfileIconData: "profileicon",
};

// Item definitions. Three of these (the Mystery Machine icon, Nubia's and the Powerpuff Girls' skins)
// are also in the retail catalog, whose entries come later in INVENTORY_DEFINITIONS and win.
export const UNRELEASED_COSMETICS_INVENTORY: InvetoryKeysDefs = Object.fromEntries(UNRELEASED_COSMETICS.map(item => [item.slug, {
  name: item.slug,
  slug: item.slug,
  type_class: "unlockable",
  max_count: 1,
  max_count_type: "strict",
  client_access: false,
  data: { EnabledForShipping: true, AssetPath: item.assetPath, DisplayName: item.displayName },
  private_data: null,
  description: "",
  log_item_transactions: true,
  tags: [ITEM_TAGS[item.assetType], "unlock_location_battlepass", "unlockable"],
  type_options: {},
  seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
  propagate_to_owner: false,
  created_at: { _hydra_unix_date: 1790960000 },
  updated_at: { _hydra_unix_date: 1790960000 },
  id: createHash("sha1").update(`unreleased-cosmetic:${item.slug}`).digest("hex").slice(0, 24),
}]));
