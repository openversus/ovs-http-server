import { randomUUID } from "node:crypto";
import { adjectives, type Config, nouns, uniqueUsernameGenerator } from "unique-username-generator";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";

const config: Config = {
  dictionaries: [adjectives, nouns],
};

// ─── Bot ID ───────────────────────────────────────────────────────────────────

export function generateBotId(): string {
  return `Bot${randomUUID()}`;
}

export function isBotId(id: string): boolean {
  return id.startsWith("Bot");
}

// ─── Cosmetics pools ──────────────────────────────────────────────────────────

const BOT_RINGOUT_VFX = [
  "ring_out_vfx_default",
  "ring_out_vfx_aku_fire",
  "ring_out_vfx_adam_west",
  "ring_out_vfx_monster_reveal",
  "ring_out_vfx_firework_show",
  "ring_out_vfx_omega_beam",
  "ring_out_vfx_bat_signal",
  "ring_out_vfx_lunar_rabbit",
  "ring_out_vfx_lasso_of_truth",
  "ring_out_vfx_boom_tube",
  "ring_out_vfx_soothing_energy",
  "ring_out_vfx_house_lannister",
  "ring_out_vfx_house_Targeryen",
  "ring_out_vfx_happy_birday_tweetie",
  "ring_out_vfx_monster_tweety",
  "ring_out_vfx_sugar_spice_everything_nice",
];

const BOT_BANNERS = [
  "banner_default",
  "banner_cheesy_temptations",
  "banner_hole_sweet_hole",
  "banner_lannister_banner",
  "banner_slushy_plushy",
  "banner_scoobtober_bats",
  "banner_presently_shocked_epic",
  "banner_marcyshouse",
  "banner_jokerhaha",
  "banner_foretold_champion_rare",
  "banner_test",
  "banner_3rror_mvs",
  "banner_marvin_wardrobe_v2",
  "banner_housetargaryen_01",
  "banner_tools_of_the_trade",
  "banner_chestnuts_not_included",
];

const BOT_PROFILE_ICONS = [
  "profile_icon_default",
  "profile_icon_dc_bat_batman_1",
  "profile_icon_wb_watertower",
  "profile_icon_cn_at_finn_adventure_time",
  "profile_icon_wb_sd_scooby_snack",
  "profile_icon_wb_sd_mysterymachine",
  "profile_icon_mvs_beach_day",
  "profile_icon_bugsbunny_thewabbit",
  "profileicon_stripe_mogwaistripe",
  "profileicon_wb_sj_oldschooljam",
  "profile_icon_c036_data_corrupted",
];

const BOT_PERKS = [
  "perk_gen_boxer",
  "perk_team_speed_force_assist",
  "perk_purest_of_motivations",
  "perk_gen_well_rounded",
];

function randomItem<T>(arr: T[]): T {
  return arr[Math.floor(Math.random() * arr.length)];
}

// ─── Config generation ────────────────────────────────────────────────────────

export function generateBotPlayerConfig(botId: string): PlayerConfig {
  return {
    AccountId: botId,
    Username: uniqueUsernameGenerator(config),
    bUseCharacterDisplayName: true,
    PlayerIndex: 0,
    TeamIndex: 0,
    Character: "",
    Skin: "",
    Taunts: [],
    Perks: BOT_PERKS,
    Banner: randomItem(BOT_BANNERS),
    ProfileIcon: randomItem(BOT_PROFILE_ICONS),
    RingoutVfx: randomItem(BOT_RINGOUT_VFX),
    bIsBot: true,
    BotBehaviorOverride: "",
    BotDifficultyMin: 2,
    BotDifficultyMax: 2,
    Buffs: [],
    StatTrackers: [],
    Gems: [],
    StartingDamage: 0,
    Handicap: 0,
    GameplayPreferences: 0,
    bAutoPartyPreference: false,
    PartyMember: null,
    PartyId: null,
    RankedTier: null,
    RankedDivision: null,
    WinStreak: null,
    IsHost: false,
    Ip: "",
  };
}
