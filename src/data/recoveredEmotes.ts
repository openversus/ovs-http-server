import { createHash } from "crypto";
import type { InvetoryKeysDefs } from "./inventoryDefs";

export const RECOVERED_EMOTES = [
  { token: "BugsCry", slug: "emote_ovs_bugs_cry", displayName: "Bugs Bunny Crying" },
  { token: "CozyTea", slug: "emote_ovs_cozy_tea", displayName: "Cozy Tea" },
  { token: "Dizzy", slug: "emote_ovs_dizzy", displayName: "Dizzy" },
  { token: "GingerbreadMan", slug: "emote_ovs_gingerbread_man", displayName: "Gingerbread Man" },
  { token: "GreenLightGo", slug: "emote_ovs_green_light_go", displayName: "Green Light Go" },
  { token: "Halo", slug: "emote_ovs_halo", displayName: "Halo" },
  { token: "Mistletoe", slug: "emote_ovs_mistletoe", displayName: "Mistletoe" },
  { token: "Popcorn", slug: "emote_ovs_popcorn", displayName: "Popcorn" },
  { token: "Snowflake", slug: "emote_ovs_snowflake", displayName: "Melting Snowflake" },
  { token: "ToastieAngry", slug: "emote_ovs_toastie_angry", displayName: "Angry Toastie" },
  { token: "ToastieBeaten", slug: "emote_ovs_toastie_beaten", displayName: "Beaten Toastie" },
  { token: "ToastieCool", slug: "emote_ovs_toastie_cool", displayName: "Cool Toastie" },
  { token: "ToastieCute", slug: "emote_ovs_toastie_cute", displayName: "Cute Toastie" },
  { token: "ToastieLol", slug: "emote_ovs_toastie_lol", displayName: "LOL Toastie" },
  { token: "ToastieShocked", slug: "emote_ovs_toastie_shocked", displayName: "Shocked Toastie" },
  { token: "ToastieSick", slug: "emote_ovs_toastie_sick", displayName: "Sick Toastie" },
  { token: "ToastieSleepy", slug: "emote_ovs_toastie_sleepy", displayName: "Sleepy Toastie" },
  { token: "ToastieStarstruck", slug: "emote_ovs_toastie_starstruck", displayName: "Star-Struck Toastie" },
  { token: "Wut", slug: "emote_ovs_wut", displayName: "Wut" },
  { token: "GizmoSmith", slug: "emote_ovs_gizmo_smith", displayName: "I Hate Mogwais" },
  { token: "TazGetIn", slug: "emote_ovs_taz_get_in", displayName: "Taz: Get In!" },
  { token: "JDawg", slug: "emote_ovs_jdawg", displayName: "J-Dawg" },
] as const;

export const RECOVERED_EMOTE_SLUGS = RECOVERED_EMOTES.map((emote) => emote.slug);
export const RECOVERED_EMOTE_SLUG_SET = new Set<string>(RECOVERED_EMOTE_SLUGS);

export const RECOVERED_EMOTE_INVENTORY: InvetoryKeysDefs = Object.fromEntries(
  RECOVERED_EMOTES.map(({ token, slug, displayName }) => {
    const root = `/OVS/Rewards/Emotes/Recovered/${token}`;
    return [slug, {
      name: slug,
      slug,
      type_class: "unlockable",
      max_count: 1,
      max_count_type: "strict",
      client_access: false,
      data: {
        EnabledForShipping: true,
        AssetPath: `${root}/${slug}.${slug}`,
        AssociatedCharacter: "Base",
        DisplayName: displayName,
        Rarity: "Rare",
        RewardThumbnail: `${root}/T_OVS_${token}_Thumbnail.T_OVS_${token}_Thumbnail`,
        RewardThumbnailMaterial: `${root}/MI_OVS_${token}_BPThumb.MI_OVS_${token}_BPThumb`,
      },
      private_data: null,
      description: "",
      log_item_transactions: true,
      tags: ["rarity_rare", "unlock_location_battlepass", "emote", "unlockable"],
      type_options: {},
      seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
      propagate_to_owner: false,
      created_at: { _hydra_unix_date: 1790308800 },
      updated_at: { _hydra_unix_date: 1790308800 },
      id: createHash("sha256").update(`ovs-emote:${slug}`).digest("hex").slice(0, 24),
    }];
  }),
);
