// Centralized defaults for bots in custom matches.
//
// Used by:
//   - modules/customLobby/lobby.service.ts (startCustomMatch — bot config + perks pre-lock)
//   - websocket.ts (handleSendGamePlayConfig — bot branch in gameplay config builder)
//
// Keep these in one place so adding/changing a default doesn't drift between
// the lobby's view of the bot and the WS-built gameplay config.

export const BOT_DEFAULT_PERKS: readonly string[] = [
  "perk_gen_boxer",
  "perk_team_speed_force_assist",
  "perk_purest_of_motivations",
  "perk_gen_well_rounded",
];

export const BOT_DEFAULT_CHARACTER = "character_jason";
export const BOT_DEFAULT_SKIN = "skin_jason_000";

// BotSettingSlug → difficulty range mapping. Used at start_custom_match time
// to translate the lobby's bot-difficulty selection into the gameplay config
// BotDifficultyMin/Max ints expected by the game.
export const BOT_DIFFICULTY: Record<string, { min: number; max: number }> = {
  VeryEasy: { min: 0, max: 0 },
  Easy:     { min: 1, max: 1 },
  Medium:   { min: 2, max: 2 },
  Hard:     { min: 3, max: 3 },
};
