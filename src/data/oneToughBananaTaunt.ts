// "One Tough Banana" (2026-10-04), the first OVS-made taunt: Banana Guard flexes, glances, nods. A
// TauntData in OVS_P made like the game's Banana Guard taunts (Taunt_BananaGuard_MamaSaid), owned by
// everyone like his other taunts (taunts.ts); its catalog row is in dotnet/tools/assets/end-game-assets.json (sync_assets.mjs). The animation and the Unreal script are in the Codex workspace:
// work/one-tough-banana and scripts/ue/create_one_tough_banana_taunt.py.
import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

export const ONE_TOUGH_BANANA_SLUG = "taunt_bananaguard_onetoughbanana";
export const ONE_TOUGH_BANANA_ASSET_PATH = "/OVS/Rewards/Taunts/OneToughBanana/taunt_bananaguard_onetoughbanana.taunt_bananaguard_onetoughbanana";
// Exactly as the game spells Banana Guard's slug.
export const ONE_TOUGH_BANANA_CHARACTER = "character_BananaGuard";

export const ONE_TOUGH_BANANA_INVENTORY: InvetoryKeysDefs = {
  [ONE_TOUGH_BANANA_SLUG]: {
    name: ONE_TOUGH_BANANA_SLUG,
    slug: ONE_TOUGH_BANANA_SLUG,
    type_class: "unlockable",
    max_count: 1,
    max_count_type: "strict",
    client_access: false,
    data: {
      AssetPath: ONE_TOUGH_BANANA_ASSET_PATH,
      AssociatedCharacter: "C034",
      DisplayName: "One Tough Banana",
      EnabledForShipping: true,
      Rarity: "Rare",
      RewardThumbnail: "/OVS/Rewards/Taunts/OneToughBanana/T_OVS_OneToughBanana_Thumbnail.T_OVS_OneToughBanana_Thumbnail",
      RewardThumbnailMaterial: "",
    },
    private_data: null,
    description: "",
    log_item_transactions: true,
    tags: ["rarity_rare", "character_c034", "universe_adventure_time", "taunt", "unlockable"],
    type_options: {},
    seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
    propagate_to_owner: false,
    created_at: { _hydra_unix_date: 1791100000 },
    updated_at: { _hydra_unix_date: 1791100000 },
    id: createHash("sha1").update(`ovs-taunt:${ONE_TOUGH_BANANA_SLUG}`).digest("hex").slice(0, 24),
  },
};
