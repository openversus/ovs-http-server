import type { InventoryDef } from "./inventoryDefs";

// This slug and path must match MyProject's OVS SkinData and OVSSlugIndex row.
export const CHROMIUM_SHAGGY_SLUG = "skin_ovs_chromium_shaggy";
export const CHROMIUM_SHAGGY_PATH =
  "/OVS/Rewards/Skins/Chromium/Shaggy/Catalog/skin_ovs_chromium_shaggy.skin_ovs_chromium_shaggy";

export const CHROMIUM_SHAGGY_ASSET = {
  slug: CHROMIUM_SHAGGY_SLUG,
  assetType: "SkinData",
  assetPath: CHROMIUM_SHAGGY_PATH,
  character_slug: "character_shaggy",
  enabled: true,
} as const;

// Shaggy's Chromium inventory definition. Ownership comes from his Fighter Pass.
export const CHROMIUM_SHAGGY_INVENTORY: InventoryDef = {
  id: "6aadea58dfda4211c94700a7",
  name: CHROMIUM_SHAGGY_SLUG,
  slug: CHROMIUM_SHAGGY_SLUG,
  type_class: "unlockable",
  max_count: 1,
  max_count_type: "strict",
  client_access: false,
  data: {
    AssetPath: CHROMIUM_SHAGGY_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "Shaggy",
    DisplayName: "Chromium Shaggy",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    RewardThumbnail: "/OVS/Rewards/Skins/Chromium/Shaggy/UI/T_Chromium_Shaggy_Portrait.T_Chromium_Shaggy_Portrait",
    RewardThumbnailMaterial: "/OVS/Rewards/Skins/Chromium/Shaggy/UI/MI_Chromium_Shaggy_Portrait.MI_Chromium_Shaggy_Portrait",
  },
  private_data: null,
  description: "Chromium finish for Shaggy.",
  log_item_transactions: true,
  tags: ["character_shaggy", "unlock_location_fighter_mastery", "universe_scooby_doo", "skin", "unlockable"],
  type_options: {},
  seed: { override_none: false, data: {}, server_data: {}, private_data: {} },
  propagate_to_owner: false,
};

// Additional Chromium skins; each is assigned to its Fighter Pass below.
export const CHROMIUM_BANANAGUARD_SLUG = "skin_ovs_chromium_bananaguard";
export const CHROMIUM_SUPERMAN_SLUG = "skin_ovs_chromium_superman";
export const CHROMIUM_PAIR = [
  {
    slug: CHROMIUM_BANANAGUARD_SLUG,
    characterSlug: "character_BananaGuard",
    folder: "BananaGuard",
    displayName: "Chromium Banana Guard",
    associatedCharacter: "C034",
    universe: "universe_adventure_time",
    id: "6aacf012ec42cfc9577c1101",
    thumbnail: "/OVS/Rewards/Skins/Chromium/BananaGuard/UI/T_Chromium_BananaGuard_Portrait.T_Chromium_BananaGuard_Portrait",
    thumbnailMaterial: "/OVS/Rewards/Skins/Chromium/BananaGuard/UI/MI_Chromium_BananaGuard_Portrait.MI_Chromium_BananaGuard_Portrait",
  },
  {
    slug: CHROMIUM_SUPERMAN_SLUG,
    characterSlug: "character_superman",
    folder: "Superman",
    displayName: "Chromium Superman",
    associatedCharacter: "Superman",
    universe: "universe_dc",
    id: "6aacf012ec42cfc9577c1102",
    thumbnail: "/OVS/Rewards/Skins/Chromium/Superman/UI/T_Chromium_Superman_Portrait.T_Chromium_Superman_Portrait",
    thumbnailMaterial: "/OVS/Rewards/Skins/Chromium/Superman/UI/MI_Chromium_Superman_Portrait.MI_Chromium_Superman_Portrait",
  },
] as const;

export const CHROMIUM_PAIR_ASSETS = CHROMIUM_PAIR.map((skin) => ({
  slug: skin.slug,
  assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug,
  enabled: true,
}));

export const CHROMIUM_PAIR_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_PAIR.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id, name: skin.slug, slug: skin.slug,
    data: {
      AssetPath: CHROMIUM_PAIR_ASSETS[index].assetPath,
      EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter,
      DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "",
      DisplayNameLocalizationNamespace: "",
      Rarity: "Epic",
      RewardThumbnail: skin.thumbnail,
      RewardThumbnailMaterial: skin.thumbnailMaterial,
    },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

// Local test variant. Keep the native Batman thumbnail until a screenshot is approved.
export const CHROMIUM_BATMAN_SLUG = "skin_ovs_chromium_batman";
export const CHROMIUM_BATMAN_PATH =
  "/OVS/Rewards/Skins/Chromium/Batman/Catalog/skin_ovs_chromium_batman.skin_ovs_chromium_batman";
export const CHROMIUM_BATMAN_ASSET = {
  slug: CHROMIUM_BATMAN_SLUG,
  assetType: "SkinData",
  assetPath: CHROMIUM_BATMAN_PATH,
  character_slug: "character_batman",
  enabled: true,
} as const;
export const CHROMIUM_BATMAN_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1103",
  name: CHROMIUM_BATMAN_SLUG,
  slug: CHROMIUM_BATMAN_SLUG,
  data: {
    AssetPath: CHROMIUM_BATMAN_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "Batman",
    DisplayName: "Chromium Batman",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    RewardThumbnail: "/Game/Character/Captures/Batman/Batman_Batman.Batman_Batman",
    RewardThumbnailMaterial: "/Game/Panda_Main/Characters/BatmanV2/Skins/MI_Batman_RewardThumbnail.MI_Batman_RewardThumbnail",
  },
  description: "Chromium finish for Batman.",
  tags: ["character_batman", "unlock_location_fighter_mastery", "universe_dc", "skin", "unlockable"],
};

// Three-character local test pilot. Native thumbnails remain until screenshots
// are approved; this does not place any item in the battle pass.
export const CHROMIUM_PILOT = [
  {
    slug: "skin_ovs_chromium_wonderwoman",
    characterSlug: "character_wonder_woman",
    folder: "WonderWoman",
    displayName: "Chromium Wonder Woman",
    associatedCharacter: "WonderWoman",
    universe: "universe_dc",
    id: "6aacf012ec42cfc9577c1104",
    thumbnail: "/Game/Character/Captures/Wonder_Woman/Wonder_Woman_Skin_WonderWoman_.Wonder_Woman_Skin_WonderWoman_",
    thumbnailMaterial: "/Game/Panda_Main/Characters/WonderWomanV2/Skins/MI_Skin_WonderWoman__RewardThumbnail.MI_Skin_WonderWoman__RewardThumbnail",
  },
  {
    slug: "skin_ovs_chromium_harleyquinn",
    characterSlug: "character_harleyquinn",
    folder: "HarleyQuinn",
    displayName: "Chromium Harley Quinn",
    associatedCharacter: "HarleyQuinn",
    universe: "universe_dc",
    id: "6aacf012ec42cfc9577c1105",
    thumbnail: "/Game/Character/Captures/Harley/Harley_HarleyWithJacket.Harley_HarleyWithJacket",
    thumbnailMaterial: "/Game/Panda_Main/Characters/HarleyQuinn/skins/MI_HarleyWithJacket_RewardThumbnail.MI_HarleyWithJacket_RewardThumbnail",
  },
  {
    slug: "skin_ovs_chromium_finn",
    characterSlug: "character_finn",
    folder: "Finn",
    displayName: "Chromium Finn",
    associatedCharacter: "Finn",
    universe: "universe_adventure_time",
    id: "6aacf012ec42cfc9577c1106",
    thumbnail: "/Game/Character/Captures/Finn/Finn_Finn_S00.Finn_Finn_S00",
    thumbnailMaterial: "/Game/Panda_Main/Characters/Finn/Skins/MI_Finn_S00_RewardThumbnail.MI_Finn_S00_RewardThumbnail",
  },
] as const;

export const CHROMIUM_PILOT_ASSETS = CHROMIUM_PILOT.map((skin) => ({
  slug: skin.slug,
  assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug,
  enabled: true,
}));

export const CHROMIUM_PILOT_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_PILOT.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id,
    name: skin.slug,
    slug: skin.slug,
    data: {
      AssetPath: CHROMIUM_PILOT_ASSETS[index].assetPath,
      EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter,
      DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "",
      DisplayNameLocalizationNamespace: "",
      Rarity: "Epic",
      RewardThumbnail: skin.thumbnail,
      RewardThumbnailMaterial: skin.thumbnailMaterial,
    },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

// Second reviewed local test batch. Native thumbnails remain until screenshots
// are approved; these entries do not alter the battle pass.
export const CHROMIUM_BATCH5 = [
  { slug: "skin_ovs_chromium_velma", characterSlug: "character_velma", folder: "Velma", displayName: "Chromium Velma", associatedCharacter: "Velma", universe: "universe_scooby_doo", id: "6aacf012ec42cfc9577c1108", thumbnail: "/Game/Character/Captures/Velma/Velma_Velma.Velma_Velma", thumbnailMaterial: "/Game/Panda_Main/Characters/Velma/Skins/MI_Velma_RewardThumbnail.MI_Velma_RewardThumbnail" },
  { slug: "skin_ovs_chromium_steven", characterSlug: "character_steven", folder: "Steven", displayName: "Chromium Steven", associatedCharacter: "StevenUniverse", universe: "universe_steven_universe", id: "6aacf012ec42cfc9577c1109", thumbnail: "/Game/Character/Captures/Steven/Steven_Steven.Steven_Steven", thumbnailMaterial: "/Game/Panda_Main/Characters/Steven/MI_Steven_RewardThumbnail.MI_Steven_RewardThumbnail" },
  { slug: "skin_ovs_chromium_jake", characterSlug: "character_jake", folder: "Jake", displayName: "Chromium Jake", associatedCharacter: "Jake", universe: "universe_adventure_time", id: "6aacf012ec42cfc9577c1110", thumbnail: "/Game/Character/Captures/Jake/Jake_Jake.Jake_Jake", thumbnailMaterial: "/Game/Panda_Main/Characters/Jake/Skins/MI_Jake_RewardThumbnail.MI_Jake_RewardThumbnail" },
  { slug: "skin_ovs_chromium_garnet", characterSlug: "character_garnet", folder: "Garnet", displayName: "Chromium Garnet", associatedCharacter: "Garnet", universe: "universe_steven_universe", id: "6aacf012ec42cfc9577c1111", thumbnail: "/Game/Character/Captures/Garnet/Garnet_Garnet.Garnet_Garnet", thumbnailMaterial: "/Game/Panda_Main/Characters/Garnet/Skins/MI_Garnet_RewardThumbnail.MI_Garnet_RewardThumbnail" },
  { slug: "skin_ovs_chromium_reindog", characterSlug: "character_creature", folder: "Reindog", displayName: "Chromium Reindog", associatedCharacter: "ReinDog", universe: "universe_pfg", id: "6aacf012ec42cfc9577c1112", thumbnail: "/Game/Character/Captures/Rein_Dog/Rein_Dog_Creature.Rein_Dog_Creature", thumbnailMaterial: "/Game/Panda_Main/Characters/Creature/Skins/MI_Creature_RewardThumbnail.MI_Creature_RewardThumbnail" },
] as const;

export const CHROMIUM_BATCH5_ASSETS = CHROMIUM_BATCH5.map((skin) => ({
  slug: skin.slug, assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug, enabled: true,
}));

export const CHROMIUM_BATCH5_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_BATCH5.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id, name: skin.slug, slug: skin.slug,
    data: { AssetPath: CHROMIUM_BATCH5_ASSETS[index].assetPath, EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter, DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "", DisplayNameLocalizationNamespace: "", Rarity: "Epic",
      RewardThumbnail: skin.thumbnail, RewardThumbnailMaterial: skin.thumbnailMaterial },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

// Third reviewed local test batch. Native thumbnails remain until approved
// screenshots replace them; these entries do not alter the battle pass.
export const CHROMIUM_BATCH6 = [
  { slug: "skin_ovs_chromium_gizmo", characterSlug: "character_C023A", folder: "Gizmo", displayName: "Chromium Gizmo", associatedCharacter: "C023A", universe: "universe_gremlins", id: "6aacf012ec42cfc9577c1113", thumbnail: "/Game/Character/Captures/C023A/C023A_C023A_S00.C023A_C023A_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C023A/Skins/Skin00/MI_C023A_S00_RewardThumbnail.MI_C023A_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_stripe", characterSlug: "character_C023B", folder: "Stripe", displayName: "Chromium Stripe", associatedCharacter: "C023B", universe: "universe_gremlins", id: "6aacf012ec42cfc9577c1114", thumbnail: "/Game/Character/Captures/C023B/C023B_C023B_S00.C023B_C023B_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C023B/Skins/S00/MI_C023B_S00_RewardThumbnail.MI_C023B_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_iron_giant", characterSlug: "character_C017", folder: "IronGiant", displayName: "Chromium Iron Giant", associatedCharacter: "C017", universe: "universe_iron_giant", id: "6aacf012ec42cfc9577c1115", thumbnail: "/Game/Character/Captures/C017/C017_C017_S00.C017_C017_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C017/Skins/S00/MI_C017_S00_RewardThumbnail.MI_C017_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_taz", characterSlug: "character_taz", folder: "Taz", displayName: "Chromium Taz", associatedCharacter: "C015", universe: "universe_looney_tunes", id: "6aacf012ec42cfc9577c1116", thumbnail: "/Game/Character/Captures/C015/C015_C015_Default.C015_C015_Default", thumbnailMaterial: "/Game/Panda_Main/Characters/C015/Skins/MI_C015_Default_RewardThumbnail.MI_C015_Default_RewardThumbnail" },
  { slug: "skin_ovs_chromium_marvin", characterSlug: "character_C018", folder: "Marvin", displayName: "Chromium Marvin", associatedCharacter: "C018", universe: "universe_looney_tunes", id: "6aacf012ec42cfc9577c1117", thumbnail: "/Game/Character/Captures/C018/C018_C018_S00.C018_C018_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C018/Skins/MI_C018_S00_RewardThumbnail.MI_C018_S00_RewardThumbnail" },
] as const;

export const CHROMIUM_BATCH6_ASSETS = CHROMIUM_BATCH6.map((skin) => ({
  slug: skin.slug, assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug, enabled: true,
}));

export const CHROMIUM_BATCH6_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_BATCH6.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id, name: skin.slug, slug: skin.slug,
    data: { AssetPath: CHROMIUM_BATCH6_ASSETS[index].assetPath, EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter, DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "", DisplayNameLocalizationNamespace: "", Rarity: "Epic",
      RewardThumbnail: skin.thumbnail, RewardThumbnailMaterial: skin.thumbnailMaterial },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

// Chromium Joker deliberately derives from the native Batman Who Laughs S03
// variant, including its alternate mesh set, presentation and voice metadata.
export const CHROMIUM_JOKER_TBWL_SLUG = "skin_ovs_chromium_joker_batman_who_laughs";
export const CHROMIUM_JOKER_TBWL_PATH =
  "/OVS/Rewards/Skins/Chromium/JokerBatmanWhoLaughs/Catalog/skin_ovs_chromium_joker_batman_who_laughs.skin_ovs_chromium_joker_batman_who_laughs";
export const CHROMIUM_JOKER_TBWL_ASSET = {
  slug: CHROMIUM_JOKER_TBWL_SLUG,
  assetType: "SkinData",
  assetPath: CHROMIUM_JOKER_TBWL_PATH,
  character_slug: "character_C028",
  enabled: true,
} as const;
export const CHROMIUM_JOKER_TBWL_INVENTORY: InventoryDef = {
  ...CHROMIUM_SHAGGY_INVENTORY,
  id: "6aacf012ec42cfc9577c1118",
  name: CHROMIUM_JOKER_TBWL_SLUG,
  slug: CHROMIUM_JOKER_TBWL_SLUG,
  data: {
    AssetPath: CHROMIUM_JOKER_TBWL_PATH,
    EnabledForShipping: true,
    AssociatedCharacter: "C028",
    DisplayName: "Chromium The Batman Who Laughs",
    DisplayNameLocalizationKey: "",
    DisplayNameLocalizationNamespace: "",
    Rarity: "Epic",
    RewardThumbnail: "/Game/Character/Captures/C028/C028_C028_S03.C028_C028_S03",
    RewardThumbnailMaterial: "/Game/Panda_Main/Characters/C028/Skins/S03/MI_C028_S03_RewardThumbnail.MI_C028_S03_RewardThumbnail",
  },
  description: "Chromium finish for The Batman Who Laughs.",
  tags: ["character_C028", "unlock_location_fighter_mastery", "universe_dc", "skin", "unlockable"],
};

// Fourth reviewed local test batch. These preserve each native skin's portrait
// until dedicated Chromium screenshots are approved and do not alter the pass.
export const CHROMIUM_BATCH7 = [
  { slug: "skin_ovs_chromium_tom_and_jerry", characterSlug: "character_tom_and_jerry", folder: "TomAndJerry", displayName: "Chromium Tom & Jerry", associatedCharacter: "TomAndJerry", universe: "universe_tom_and_jerry", id: "6aacf012ec42cfc9577c1119", thumbnail: "/Game/Character/Captures/Tom_and_Jerry/Tom_and_Jerry_TomAndJerry_S00.Tom_and_Jerry_TomAndJerry_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/TomAndJerry/Skins/MI_TomAndJerry_S00_RewardThumbnail.MI_TomAndJerry_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_black_adam", characterSlug: "character_C021", folder: "BlackAdam", displayName: "Chromium Black Adam", associatedCharacter: "C021", universe: "universe_dc", id: "6aacf012ec42cfc9577c1120", thumbnail: "/Game/Character/Captures/C021/C021_C021_S00.C021_C021_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C021/Skins/MI_C021_S00_RewardThumbnail.MI_C021_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_rick", characterSlug: "character_C020", folder: "Rick", displayName: "Chromium Rick", associatedCharacter: "C020", universe: "universe_rick_and_morty", id: "6aacf012ec42cfc9577c1121", thumbnail: "/Game/Character/Captures/C020/C020_C020_S00.C020_C020_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C020/Skins/MI_C020_S00_RewardThumbnail.MI_C020_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_morty", characterSlug: "character_c019", folder: "Morty", displayName: "Chromium Morty", associatedCharacter: "C019", universe: "universe_rick_and_morty", id: "6aacf012ec42cfc9577c1122", thumbnail: "/Game/Character/Captures/C019/C019_C019_S00.C019_C019_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C019/Skins/MI_C019_S00_RewardThumbnailMaterial.MI_C019_S00_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_lebron", characterSlug: "character_c16", folder: "LeBron", displayName: "Chromium LeBron", associatedCharacter: "C016", universe: "universe_space_jam", id: "6aacf012ec42cfc9577c1123", thumbnail: "/Game/Panda_Main/Characters/c016/UI/FullPortrait_C016_S00.FullPortrait_C016_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/c016/UI/MI_C016_S00_VariantIcon.MI_C016_S00_VariantIcon" },
  { slug: "skin_ovs_chromium_bugs_bunny", characterSlug: "character_bugs_bunny", folder: "BugsBunny", displayName: "Chromium Bugs Bunny", associatedCharacter: "BugsBunny", universe: "universe_looney_tunes", id: "6aacf012ec42cfc9577c1124", thumbnail: "/Game/Character/Captures/Bugs_Bunny/Bugs_Bunny_BugsBunny.Bugs_Bunny_BugsBunny", thumbnailMaterial: "/Game/Panda_Main/Characters/BugsBunnyV2/Skins/MI_BugsBunny_RewardThumbnail.MI_BugsBunny_RewardThumbnail" },
  { slug: "skin_ovs_chromium_jason_finn", characterSlug: "character_Jason", folder: "JasonFinn", displayName: "Chromium Finn Jason", associatedCharacter: "C035", universe: "universe_friday_the_13th", id: "6aacf012ec42cfc9577c1125", thumbnail: "/MvsSeason03/Character/Jason/Skins/S02/T_C035_S02.T_C035_S02", thumbnailMaterial: "/MvsSeason03/Character/Jason/Skins/S02/MI_C035_S02_RewardThumbnailMaterial.MI_C035_S02_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_arya", characterSlug: "character_arya", folder: "Arya", displayName: "Chromium Arya Stark", associatedCharacter: "Arya", universe: "universe_game_of_thrones", id: "6aacf012ec42cfc9577c1126", thumbnail: "/Game/Character/Captures/Arya/Arya_C006.Arya_C006", thumbnailMaterial: "/Game/Panda_Main/Characters/Arya/Skins/MI_C006_RewardThumbnailMaterial.MI_C006_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_agent_smith", characterSlug: "character_c036", folder: "AgentSmith", displayName: "Chromium Agent Smith", associatedCharacter: "C036", universe: "universe_matrix", id: "6aacf012ec42cfc9577c1127", thumbnail: "/Game/Character/Captures/C036/C036_C036_Skin_000.C036_C036_Skin_000", thumbnailMaterial: "/Game/Character/C036/Skins/MI_C036_Skin_000_RewardThumbnailMaterial.MI_C036_Skin_000_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_powerpuff_girls", characterSlug: "character_C030", folder: "PowerpuffGirls", displayName: "Chromium Powerpuff Girls", associatedCharacter: "C030", universe: "universe_powerpuff_girls", id: "6aacf012ec42cfc9577c1128", thumbnail: "/MvsSeason03/Character/C030/UI/t_fighterthumbnail_powerpuffgirls.t_fighterthumbnail_powerpuffgirls", thumbnailMaterial: "/MvsSeason03/Character/C030/Skins/MI_C030_S00_RewardThumbnailMaterial.MI_C030_S00_RewardThumbnailMaterial" },
] as const;

export const CHROMIUM_BATCH7_ASSETS = CHROMIUM_BATCH7.map((skin) => ({
  slug: skin.slug,
  assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug,
  enabled: true,
}));

export const CHROMIUM_BATCH7_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_BATCH7.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id,
    name: skin.slug,
    slug: skin.slug,
    data: {
      AssetPath: CHROMIUM_BATCH7_ASSETS[index].assetPath,
      EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter,
      DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "",
      DisplayNameLocalizationNamespace: "",
      Rarity: "Epic",
      RewardThumbnail: skin.thumbnail,
      RewardThumbnailMaterial: skin.thumbnailMaterial,
    },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

// Final reviewed Chromium roster batch. Native portraits remain in use until
// dedicated screenshots are approved; these entries do not alter the pass.
export const CHROMIUM_BATCH8 = [
  { slug: "skin_ovs_chromium_nubia", characterSlug: "character_C027", folder: "Nubia", displayName: "Chromium Nubia", associatedCharacter: "C027", universe: "universe_dc", id: "6aacf012ec42cfc9577c1129", thumbnail: "/Game/Panda_Main/Characters/C027/Skins/S00/T_C027_S00.T_C027_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C027/Skins/S00/MI_C027_S00_RewardThumbnail.MI_C027_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_samurai_jack", characterSlug: "character_C026", folder: "SamuraiJack", displayName: "Chromium Samurai Jack", associatedCharacter: "C026", universe: "universe_samurai_jack", id: "6aacf012ec42cfc9577c1130", thumbnail: "/Game/Character/Captures/C026/C026_C026_S00.C026_C026_S00", thumbnailMaterial: "/Game/Panda_Main/Characters/C026/MI_C026_S00_RewardThumbnail.MI_C026_S00_RewardThumbnail" },
  { slug: "skin_ovs_chromium_betelgeuse", characterSlug: "character_c024", folder: "Betelgeuse", displayName: "Chromium Betelgeuse", associatedCharacter: "C024", universe: "universe_beetlejuice", id: "6aacf012ec42cfc9577c1131", thumbnail: "/Game/Character/Captures/C024/C024_C024_S00.C024_C024_S00", thumbnailMaterial: "/Game/Character/C024/Skins/S00/MI_C024_Skin_000_RewardThumbnailMaterial.MI_C024_Skin_000_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_raven", characterSlug: "character_C025", folder: "Raven", displayName: "Chromium Raven", associatedCharacter: "C025", universe: "universe_dc", id: "6aacf012ec42cfc9577c1132", thumbnail: "/Character_C025/Character/C025/Skins/T_C025_S00.T_C025_S00", thumbnailMaterial: "/Character_C025/Character/C025/Skins/MI_C025_Skin_000_RewardThumbnailMaterial.MI_C025_Skin_000_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_marceline", characterSlug: "character_C031", folder: "Marceline", displayName: "Chromium Marceline", associatedCharacter: "C031", universe: "universe_adventure_time", id: "6aacf012ec42cfc9577c1133", thumbnail: "/Character_C031/C031/Skins/C031_placeholder_capture.C031_placeholder_capture", thumbnailMaterial: "/Character_C031/C031/Skins/MI_C031_Skin_000_RewardThumbnailMaterial.MI_C031_Skin_000_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_aquaman", characterSlug: "character_C029", folder: "Aquaman", displayName: "Chromium Aquaman", associatedCharacter: "C029", universe: "universe_dc", id: "6aacf012ec42cfc9577c1134", thumbnail: "/CharacterC029/C029/aquaman_roster.aquaman_roster", thumbnailMaterial: "/CharacterC029/C029/Skin/MI_C029_S00_RewardThumbnailMaterial.MI_C029_S00_RewardThumbnailMaterial" },
  { slug: "skin_ovs_chromium_lola_bunny", characterSlug: "character_c038", folder: "LolaBunny", displayName: "Chromium Lola Bunny", associatedCharacter: "BugsBunny", universe: "universe_looney_tunes", id: "6aacf012ec42cfc9577c1135", thumbnail: "/Character_C038/lola_roster_02.lola_roster_02", thumbnailMaterial: "/Game/Panda_Main/Characters/BugsBunnyV2/Skins/MI_BugsBunny_RewardThumbnail.MI_BugsBunny_RewardThumbnail" },
] as const;

export const CHROMIUM_BATCH8_ASSETS = CHROMIUM_BATCH8.map((skin) => ({
  slug: skin.slug,
  assetType: "SkinData",
  assetPath: `/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`,
  character_slug: skin.characterSlug,
  enabled: true,
}));

export const CHROMIUM_BATCH8_INVENTORY: Record<string, InventoryDef> = Object.fromEntries(
  CHROMIUM_BATCH8.map((skin, index) => [skin.slug, {
    ...CHROMIUM_SHAGGY_INVENTORY,
    id: skin.id,
    name: skin.slug,
    slug: skin.slug,
    data: {
      AssetPath: CHROMIUM_BATCH8_ASSETS[index].assetPath,
      EnabledForShipping: true,
      AssociatedCharacter: skin.associatedCharacter,
      DisplayName: skin.displayName,
      DisplayNameLocalizationKey: "",
      DisplayNameLocalizationNamespace: "",
      Rarity: "Epic",
      RewardThumbnail: skin.thumbnail,
      RewardThumbnailMaterial: skin.thumbnailMaterial,
    },
    description: `${skin.displayName} finish.`,
    tags: [skin.characterSlug, "unlock_location_fighter_mastery", skin.universe, "skin", "unlockable"],
  }]),
);

/** Stable complete Chromium roster order. */
export const CHROMIUM_SKIN_SLUGS = [
  CHROMIUM_SHAGGY_SLUG,
  ...CHROMIUM_PAIR.map((skin) => skin.slug),
  CHROMIUM_BATMAN_SLUG,
  ...CHROMIUM_PILOT.map((skin) => skin.slug),
  ...CHROMIUM_BATCH5.map((skin) => skin.slug),
  ...CHROMIUM_BATCH6.map((skin) => skin.slug),
  CHROMIUM_JOKER_TBWL_SLUG,
  ...CHROMIUM_BATCH7.map((skin) => skin.slug),
  ...CHROMIUM_BATCH8.map((skin) => skin.slug),
] as const;

export const CHROMIUM_SKIN_SLUG_SET = new Set<string>(CHROMIUM_SKIN_SLUGS);

/**
 * Chromium is character-mastery content, not seasonal-pass content. Keep the
 * mapping explicit because client character slugs have inconsistent casing
 * and several mastery tracks use internal character codes.
 */
export const CHROMIUM_FIGHTER_PASS = [
  { characterSlug: "character_shaggy", trackSlug: "mrt_mastery_shaggy", skinSlug: CHROMIUM_SHAGGY_SLUG },
  { characterSlug: "character_BananaGuard", trackSlug: "mrt_mastery_banana_guard", skinSlug: CHROMIUM_BANANAGUARD_SLUG },
  { characterSlug: "character_superman", trackSlug: "mrt_mastery_c003", skinSlug: CHROMIUM_SUPERMAN_SLUG },
  { characterSlug: "character_batman", trackSlug: "mrt_mastery_batman", skinSlug: CHROMIUM_BATMAN_SLUG },
  { characterSlug: "character_wonder_woman", trackSlug: "mrt_mastery_wonder_woman", skinSlug: "skin_ovs_chromium_wonderwoman" },
  { characterSlug: "character_harleyquinn", trackSlug: "mrt_mastery_harleyquinn", skinSlug: "skin_ovs_chromium_harleyquinn" },
  { characterSlug: "character_finn", trackSlug: "mrt_mastery_finn", skinSlug: "skin_ovs_chromium_finn" },
  { characterSlug: "character_velma", trackSlug: "mrt_mastery_velma", skinSlug: "skin_ovs_chromium_velma" },
  { characterSlug: "character_steven", trackSlug: "mrt_mastery_steven", skinSlug: "skin_ovs_chromium_steven" },
  { characterSlug: "character_jake", trackSlug: "mrt_mastery_jake", skinSlug: "skin_ovs_chromium_jake" },
  { characterSlug: "character_garnet", trackSlug: "mrt_mastery_garnet", skinSlug: "skin_ovs_chromium_garnet" },
  { characterSlug: "character_creature", trackSlug: "mrt_mastery_creature", skinSlug: "skin_ovs_chromium_reindog" },
  { characterSlug: "character_C023A", trackSlug: "mrt_mastery_c023a", skinSlug: "skin_ovs_chromium_gizmo" },
  { characterSlug: "character_C023B", trackSlug: "mrt_mastery_c023b", skinSlug: "skin_ovs_chromium_stripe" },
  { characterSlug: "character_C017", trackSlug: "mrt_mastery_c017", skinSlug: "skin_ovs_chromium_iron_giant" },
  { characterSlug: "character_taz", trackSlug: "mrt_mastery_taz", skinSlug: "skin_ovs_chromium_taz" },
  { characterSlug: "character_C018", trackSlug: "mrt_mastery_c018", skinSlug: "skin_ovs_chromium_marvin" },
  { characterSlug: "character_C028", trackSlug: "mrt_mastery_c028", skinSlug: CHROMIUM_JOKER_TBWL_SLUG },
  { characterSlug: "character_tom_and_jerry", trackSlug: "mrt_mastery_tom_and_jerry", skinSlug: "skin_ovs_chromium_tom_and_jerry" },
  { characterSlug: "character_C021", trackSlug: "mrt_mastery_c021", skinSlug: "skin_ovs_chromium_black_adam" },
  { characterSlug: "character_C020", trackSlug: "mrt_mastery_c020", skinSlug: "skin_ovs_chromium_rick" },
  { characterSlug: "character_c019", trackSlug: "mrt_mastery_c019", skinSlug: "skin_ovs_chromium_morty" },
  { characterSlug: "character_c16", trackSlug: "mrt_mastery_lebron", skinSlug: "skin_ovs_chromium_lebron" },
  { characterSlug: "character_bugs_bunny", trackSlug: "mrt_mastery_bugs_bunny", skinSlug: "skin_ovs_chromium_bugs_bunny" },
  { characterSlug: "character_Jason", trackSlug: "mrt_mastery_jason", skinSlug: "skin_ovs_chromium_jason_finn" },
  { characterSlug: "character_arya", trackSlug: "mrt_mastery_arya", skinSlug: "skin_ovs_chromium_arya" },
  { characterSlug: "character_c036", trackSlug: "mrt_mastery_c036", skinSlug: "skin_ovs_chromium_agent_smith" },
  { characterSlug: "character_C030", trackSlug: "mrt_mastery_c030", skinSlug: "skin_ovs_chromium_powerpuff_girls" },
  { characterSlug: "character_C027", trackSlug: "mrt_mastery_c027", skinSlug: "skin_ovs_chromium_nubia" },
  { characterSlug: "character_C026", trackSlug: "mrt_mastery_c026", skinSlug: "skin_ovs_chromium_samurai_jack" },
  { characterSlug: "character_c024", trackSlug: "mrt_mastery_c024", skinSlug: "skin_ovs_chromium_betelgeuse" },
  { characterSlug: "character_C025", trackSlug: "mrt_mastery_c025", skinSlug: "skin_ovs_chromium_raven" },
  { characterSlug: "character_C031", trackSlug: "mrt_mastery_c031", skinSlug: "skin_ovs_chromium_marceline" },
  { characterSlug: "character_C029", trackSlug: "mrt_mastery_c029", skinSlug: "skin_ovs_chromium_aquaman" },
  { characterSlug: "character_c038", trackSlug: "mrt_mastery_c038", skinSlug: "skin_ovs_chromium_lola_bunny" },
] as const;

export const CHROMIUM_MASTERY_TRACK_SLUGS: string[] = CHROMIUM_FIGHTER_PASS.map((entry) => entry.trackSlug);
export const CHROMIUM_SKIN_BY_MASTERY_TRACK = new Map<string, string>(
  CHROMIUM_FIGHTER_PASS.map((entry) => [entry.trackSlug, entry.skinSlug]),
);

export function getChromiumMasteryTrackForCharacter(characterSlug: string): string | null {
  const normalizedForLookup = characterSlug.toLowerCase();
  return CHROMIUM_FIGHTER_PASS.find(
    (entry) => entry.characterSlug.toLowerCase() === normalizedForLookup,
  )?.trackSlug ?? null;
}

