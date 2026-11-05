import assert from "node:assert/strict";
import test from "node:test";
import {
  compareClientVersions,
  isClientGameplayAccessRequiredForMinimum,
  isClientUpdateRequiredForMinimum,
} from "../src/services/clientVersion";

test("minimum-version policy treats missing legacy versions as outdated", () => {
  assert.equal(isClientUpdateRequiredForMinimum("", "2026.09.11.1"), true);
  assert.equal(isClientUpdateRequiredForMinimum("Unknown", "2026.09.11.1"), true);
});

test("minimum-version policy compares four-part versions", () => {
  assert.equal(isClientUpdateRequiredForMinimum("2026.09.11.0", "2026.09.11.1"), true);
  assert.equal(isClientUpdateRequiredForMinimum("2026.09.11.1", "2026.09.11.1"), false);
  assert.equal(isClientUpdateRequiredForMinimum("2026.09.12.0", "2026.09.11.1"), false);
});

test("published-release comparison never treats a newer client as outdated", () => {
  assert.equal(compareClientVersions("2026.04.08.14", "2026.04.08.14"), 0);
  assert.equal(compareClientVersions("2026.04.08.15", "2026.04.08.14"), 1);
  assert.equal(compareClientVersions("2026.04.08.13", "2026.04.08.14"), -1);
});

test("an unset or invalid minimum keeps rollout enforcement disabled", () => {
  assert.equal(isClientUpdateRequiredForMinimum("", ""), false);
  assert.equal(isClientUpdateRequiredForMinimum("", "not-a-version"), false);
});

test("gameplay requires an /api/identify registration even without a version minimum", () => {
  assert.equal(isClientGameplayAccessRequiredForMinimum("2026.09.23.1", "", false, true), true);
  assert.equal(isClientGameplayAccessRequiredForMinimum("", "", false, true), true);
  assert.equal(isClientGameplayAccessRequiredForMinimum("2026.09.23.1", "", true, true), false);
});

test("identified clients must still satisfy the configured minimum", () => {
  assert.equal(isClientGameplayAccessRequiredForMinimum("2026.09.23.0", "2026.09.23.1", true, true), true);
  assert.equal(isClientGameplayAccessRequiredForMinimum("2026.09.23.1", "2026.09.23.1", true, true), false);
});

test("CLIENT_VERSION_CHECK=false lets every client through, including unidentified ones", () => {
  assert.equal(isClientGameplayAccessRequiredForMinimum("", "2026.09.11.1", false, false), false);
  assert.equal(isClientGameplayAccessRequiredForMinimum("2026.01.01.0", "2026.09.11.1", true, false), false);
  assert.equal(isClientGameplayAccessRequiredForMinimum("", "2026.09.11.1", false, true), true);
});
