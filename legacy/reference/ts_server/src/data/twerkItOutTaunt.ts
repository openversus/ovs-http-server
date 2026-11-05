// "Twerk It Out" (2026-10-07), Morty's OVS-made taunt, by Tuggernuts: a hands-on-knees bounce driven by the back and
// belly. A TauntData in OVS_P made like One Tough Banana (data/oneToughBananaTaunt.ts): its own FAD, the native Morty
// taunt's cancel windows. A battle pass reward (milestones.ts), owned once claimed; its catalog row is in
// dotnet/tools/assets/end-game-assets.json (sync_assets.mjs). The animation and the Unreal script are in the Codex
// workspace: outputs/morty-knee-bounce and scripts/ue/create_twerk_it_out_taunt.py.
import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

export const TWERK_IT_OUT_SLUG = "taunt_morty_twerkitout";
export const TWERK_IT_OUT_ASSET_PATH = "/OVS/Rewards/Taunts/TwerkItOut/taunt_morty_twerkitout.taunt_morty_twerkitout";
// Exactly as the game spells Morty's slug.
export const TWERK_IT_OUT_CHARACTER = "character_c019";

export const TWERK_IT_OUT_INVENTORY: InvetoryKeysDefs = {
  [TWERK_IT_OUT_SLUG]: {
    name: TWERK_IT_OUT_SLUG,
    slug: TWERK_IT_OUT_SLUG,
    type_class: "unlockable",
    max_count: 1,
    max_count_type: "strict",
    client_access: false,
    data: {
      AssetPath: TWERK_IT_OUT_ASSET_PATH,
      AssociatedCharacter: "C019",
      DisplayName: "Twerk It Out",
      EnabledForShipping: true,
      Rarity: "Rare",
      RewardThumbnail: "/OVS/Rewards/Taunts/TwerkItOut/T_OVS_TwerkItOut_Thumbnail.T_OVS_TwerkItOut_Thumbnail",
      RewardThumbnailMaterial: "",
    },
    private_data: null,
    description: "",
    log_item_transactions: true,
    tags: ["rarity_rare", "character_c019", "universe_rick_and_morty", "taunt", "unlockable"],
    type_options: {},
    seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
    propagate_to_owner: false,
    created_at: { _hydra_unix_date: 1791360000 },
    updated_at: { _hydra_unix_date: 1791360000 },
    id: createHash("sha1").update(`ovs-taunt:${TWERK_IT_OUT_SLUG}`).digest("hex").slice(0, 24),
  },
};
