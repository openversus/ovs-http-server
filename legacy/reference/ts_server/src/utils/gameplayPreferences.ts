// GameplayPreferences carries the player's own input settings: stick deadzones, the input buffer, whether an attack
// press picks up items. The value the game sends is the player's, and it must reach every match unchanged; a wrong one
// changes how the game feels. 0 is a real value (a few players have it), not a missing one.
//
// The game sends its current value in every party-lobby request (create_party_lobby at each login, lock_lobby_loadout,
// set_ready_for_lobby, set_lobby_joinable, set_lobby_not_joinable) and rarely, if ever, calls update_player_preferences:
// the server keeps the value it last saw (services/gameplayPreferences.ts) and every match reads that one.

/** What the server assumes for a player whose value it has never seen (the new-account default). */
export const DEFAULT_GAMEPLAY_PREFERENCES = 964;

/**
 * The value as the whole number it is: a number that is an integer, or text of an integer written plainly (digits, an
 * optional minus). Anything else (missing, null, "", "1e3", "0x10", "abc", 1.5) is null: not a value to store.
 */
export function parseGameplayPreferences(value: unknown): number | null {
  if (typeof value === "number") return Number.isSafeInteger(value) ? value : null;
  if (typeof value === "string" && /^-?\d+$/.test(value)) {
    const n = Number(value);
    return Number.isSafeInteger(n) ? n : null;
  }
  return null;
}

/** The value to play with: the stored one when it is a value (0 included), else the default. */
export function gameplayPreferencesOf(value: unknown): number {
  return parseGameplayPreferences(value) ?? DEFAULT_GAMEPLAY_PREFERENCES;
}
