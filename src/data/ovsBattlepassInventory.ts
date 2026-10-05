import type { InvetoryKeysDefs } from "./inventoryDefs";

export const SHIRTLESS_TATTOO_SHAGGY_SLUG = "skin_ovs_shirtless_tattoo_shaggy";
export const SHIRTLESS_TATTOO_SHAGGY_PATH =
  "/OVS/Rewards/Skins/Battlepass/ShirtlessTattooShaggy/Catalog/skin_ovs_shirtless_tattoo_shaggy.skin_ovs_shirtless_tattoo_shaggy";
export const SHIRTLESS_TATTOO_SHAGGY_ASSET = {
  slug: SHIRTLESS_TATTOO_SHAGGY_SLUG,
  assetType: "SkinData",
  assetPath: SHIRTLESS_TATTOO_SHAGGY_PATH,
  character_slug: "character_shaggy",
  enabled: true,
} as const;

// OVS rewards restored from bp-and-toasts; paths match the shipped OVS pak.
// Keep unrelated test characters and diagnostic aliases out of this catalog.
export const OVS_BATTLEPASS_INVENTORY: InvetoryKeysDefs = {
  "emote_67_hands": {
    "name": "emote_67_hands",
    "slug": "emote_67_hands",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/Emotes/SixSeven/emote_67_hands.emote_67_hands",
      "AssociatedCharacter": "Base",
      "DisplayName": "6-7 Hands",
      "DisplayNameLocalizationKey": "4AE252844461CAE9A21BD196EDE8E7F3",
      "DisplayNameLocalizationNamespace": "[9F9DC94F49700B3E97BC3FBB054B26C6]",
      "Rarity": "Rare",
      "RewardThumbnail": "/OVS/Rewards/Emotes/SixSeven/T_67_Hands.T_67_Hands",
      "RewardThumbnailMaterial": ""
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "emote",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1714587810
    },
    "updated_at": {
      "_hydra_unix_date": 1714587810
    },
    "id": "663288a2358a2a62107ca9c3"
  },
  "emote_ovs_pleading_cat": {
    "name": "emote_ovs_pleading_cat",
    "slug": "emote_ovs_pleading_cat",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/Emotes/PleadingCat/emote_ovs_pleading_cat.emote_ovs_pleading_cat",
      "AssociatedCharacter": "Base",
      "DisplayName": "Cute Cat Eyes",
      "Rarity": "Rare",
      "RewardThumbnail": "/OVS/Rewards/Emotes/PleadingCat/T_OVS_PleadingCat_Thumbnail.T_OVS_PleadingCat_Thumbnail",
      "RewardThumbnailMaterial": "/OVS/Rewards/Emotes/PleadingCat/MI_OVS_PleadingCat_BPThumb.MI_OVS_PleadingCat_BPThumb"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "emote",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1747000000
    },
    "updated_at": {
      "_hydra_unix_date": 1747000000
    },
    "id": "663288a2358a2a62107ca9c5"
  },
  "emote_ovs_rickflick": {
    "name": "emote_ovs_rickflick",
    "slug": "emote_ovs_rickflick",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/Emotes/RickFlick/emote_ovs_rickflick.emote_ovs_rickflick",
      "AssociatedCharacter": "Base",
      "DisplayName": "Peace Among Worlds",
      "Rarity": "Rare",
      "RewardThumbnail": "/OVS/Rewards/Emotes/RickFlick/T_OVS_RickFlick_Thumbnail.T_OVS_RickFlick_Thumbnail",
      "RewardThumbnailMaterial": "/OVS/Rewards/Emotes/RickFlick/MI_OVS_RickFlick_BPThumb.MI_OVS_RickFlick_BPThumb"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "emote",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1747000000
    },
    "updated_at": {
      "_hydra_unix_date": 1747000000
    },
    "id": "663288a2358a2a62107ca9c6"
  },
  "profileicon_ovs_duck_season": {
    "name": "profileicon_ovs_duck_season",
    "slug": "profileicon_ovs_duck_season",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/ProfileIcons/DuckSeason/profileicon_ovs_duck_season.profileicon_ovs_duck_season",
      "DisplayName": "Duck Season",
      "Rarity": "Rare",
      "TextureRef": "/OVS/Rewards/ProfileIcons/DuckSeason/T_OVS_DuckSeason_Icon.T_OVS_DuckSeason_Icon",
      "RewardThumbnail": "/OVS/Rewards/ProfileIcons/DuckSeason/T_OVS_DuckSeason_Icon.T_OVS_DuckSeason_Icon"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "profileicon",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1747000000
    },
    "updated_at": {
      "_hydra_unix_date": 1747000000
    },
    "id": "663288a2358a2a62107ca9c7"
  },
  "profileicon_ovs_icy_glare": {
    "name": "profileicon_ovs_icy_glare",
    "slug": "profileicon_ovs_icy_glare",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/ProfileIcons/IcyGlare/profileicon_ovs_icy_glare.profileicon_ovs_icy_glare",
      "DisplayName": "Icy Glare",
      "Rarity": "Rare",
      "TextureRef": "/OVS/Rewards/ProfileIcons/IcyGlare/T_OVS_IcyGlare_Icon.T_OVS_IcyGlare_Icon",
      "RewardThumbnail": "/OVS/Rewards/ProfileIcons/IcyGlare/T_OVS_IcyGlare_Icon.T_OVS_IcyGlare_Icon"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "profileicon",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1790890000
    },
    "updated_at": {
      "_hydra_unix_date": 1790890000
    },
    "id": "663288a2358a2a62107ca9c9"
  },
  // StressInducer's icons (2026-10-02); the creator credit is in the assets' CustomTags.
  "profileicon_ovs_batmobile": {
    "name": "profileicon_ovs_batmobile",
    "slug": "profileicon_ovs_batmobile",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/ProfileIcons/Batmobile/profileicon_ovs_batmobile.profileicon_ovs_batmobile",
      "DisplayName": "The Dark Knight's Batmobile Cannot Be Stopped",
      "Rarity": "Rare",
      "TextureRef": "/OVS/Rewards/ProfileIcons/Batmobile/T_OVS_Batmobile_Icon.T_OVS_Batmobile_Icon",
      "RewardThumbnail": "/OVS/Rewards/ProfileIcons/Batmobile/T_OVS_Batmobile_Icon.T_OVS_Batmobile_Icon"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "profileicon",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1790960000
    },
    "updated_at": {
      "_hydra_unix_date": 1790960000
    },
    "id": "663288a2358a2a62107caa1e"
  },
  "profileicon_ovs_jason": {
    "name": "profileicon_ovs_jason",
    "slug": "profileicon_ovs_jason",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/ProfileIcons/JaSON/profileicon_ovs_jason.profileicon_ovs_jason",
      "DisplayName": "jaSON",
      "Rarity": "Rare",
      "TextureRef": "/OVS/Rewards/ProfileIcons/JaSON/T_OVS_JaSON_Icon.T_OVS_JaSON_Icon",
      "RewardThumbnail": "/OVS/Rewards/ProfileIcons/JaSON/T_OVS_JaSON_Icon.T_OVS_JaSON_Icon"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "profileicon",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1790960000
    },
    "updated_at": {
      "_hydra_unix_date": 1790960000
    },
    "id": "663288a2358a2a62107caa1f"
  },
  "profileicon_ovs_multiversus_tattoo": {
    "name": "profileicon_ovs_multiversus_tattoo",
    "slug": "profileicon_ovs_multiversus_tattoo",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/ProfileIcons/MultiVersusTattoo/profileicon_ovs_multiversus_tattoo.profileicon_ovs_multiversus_tattoo",
      "DisplayName": "MultiVersus Ink",
      "Rarity": "Rare",
      "TextureRef": "/OVS/Rewards/ProfileIcons/MultiVersusTattoo/T_OVS_MultiVersusTattoo_Icon.T_OVS_MultiVersusTattoo_Icon",
      "RewardThumbnail": "/OVS/Rewards/ProfileIcons/MultiVersusTattoo/T_OVS_MultiVersusTattoo_Icon.T_OVS_MultiVersusTattoo_Icon"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_rare",
      "unlock_location_battlepass",
      "profileicon",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1791150000
    },
    "updated_at": {
      "_hydra_unix_date": 1791150000
    },
    "id": "8dfa0ba00e058bdb7921a5cf"
  },
  "skin_ovs_omniman_superman": {
    "name": "skin_ovs_omniman_superman",
    "slug": "skin_ovs_omniman_superman",
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": "/OVS/Rewards/Skins/Superman/OmniMan/Catalog/skin_ovs_omniman_superman.skin_ovs_omniman_superman",
      "AssociatedCharacter": "Superman",
      "DisplayName": "Supraman",
      "Rarity": "Epic"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_epic",
      "unlock_location_battlepass",
      "skin",
      "character_superman",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": {
      "_hydra_unix_date": 1748000000
    },
    "updated_at": {
      "_hydra_unix_date": 1748000000
    },
    "id": "663288a2358a2a62107ca9c8"
  },
  [SHIRTLESS_TATTOO_SHAGGY_SLUG]: {
    "name": SHIRTLESS_TATTOO_SHAGGY_SLUG,
    "slug": SHIRTLESS_TATTOO_SHAGGY_SLUG,
    "type_class": "unlockable",
    "max_count": 1,
    "max_count_type": "strict",
    "client_access": false,
    "data": {
      "EnabledForShipping": true,
      "AssetPath": SHIRTLESS_TATTOO_SHAGGY_PATH,
      "AssociatedCharacter": "Shaggy",
      "DisplayName": "Shirtless Tattoo Shaggy",
      "DisplayNameLocalizationKey": "",
      "DisplayNameLocalizationNamespace": "",
      "Rarity": "Epic"
    },
    "private_data": null,
    "description": "",
    "log_item_transactions": true,
    "tags": [
      "rarity_epic",
      "unlock_location_battlepass",
      "skin",
      "character_shaggy",
      "unlockable"
    ],
    "type_options": {},
    "seed": {
      "override_none": false,
      "data": {},
      "server_data": {},
      "private_data": {}
    },
    "propagate_to_owner": false,
    "created_at": { "_hydra_unix_date": 1790892000 },
    "updated_at": { "_hydra_unix_date": 1790892000 },
    "id": "663288a2358a2a62107ca9ca"
  }
};
