import assert from "node:assert/strict";
import test from "node:test";
import { DEFAULT_GAMEPLAY_PREFERENCES, gameplayPreferencesOf, parseGameplayPreferences } from "../src/utils/gameplayPreferences";

test("0 is a value, not a missing one", () => {
  assert.equal(parseGameplayPreferences(0), 0);
  assert.equal(parseGameplayPreferences("0"), 0);
  assert.equal(gameplayPreferencesOf(0), 0);
  assert.equal(gameplayPreferencesOf("0"), 0);
});

test("the player's value is kept as sent", () => {
  for (const v of [448, 964, 991, 4]) {
    assert.equal(gameplayPreferencesOf(v), v);
    assert.equal(gameplayPreferencesOf(String(v)), v);
  }
});

test("anything that is not a whole number is no value", () => {
  for (const v of [undefined, null, "", " 448", "448 ", "1e3", "0x10", "abc", 1.5, Number.NaN, Infinity, true, {}, [448]]) {
    assert.equal(parseGameplayPreferences(v), null, `${JSON.stringify(v)}`);
    assert.equal(gameplayPreferencesOf(v), DEFAULT_GAMEPLAY_PREFERENCES);
  }
});
