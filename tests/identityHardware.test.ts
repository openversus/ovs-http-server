import assert from "node:assert/strict";
import test from "node:test";
import { chooseUnambiguousLegacyIpCandidate, normalizeHardwareSignal } from "../src/services/identityNormalization";

const VALID_HASH = "A".repeat(64);

test("accepts only a strong, explicitly versioned V2 hardware signal", () => {
  assert.deepEqual(normalizeHardwareSignal(VALID_HASH, "2", "strong"), {
    hardwareId: VALID_HASH.toLowerCase(),
    hardwareIdVersion: "2",
    hardwareIdQuality: "strong",
  });
});

test("rejects legacy hashes even when they look like SHA-256", () => {
  assert.deepEqual(normalizeHardwareSignal(VALID_HASH, "", ""), {
    hardwareId: "",
    hardwareIdVersion: "",
    hardwareIdQuality: "",
  });
});

test("rejects weak, malformed, or placeholder hardware signals", () => {
  for (const sample of [
    normalizeHardwareSignal(VALID_HASH, "2", "weak"),
    normalizeHardwareSignal("unknown", "2", "strong"),
    normalizeHardwareSignal("1234", "2", "strong"),
    normalizeHardwareSignal(VALID_HASH, "1", "strong"),
  ]) {
    assert.deepEqual(sample, {
      hardwareId: "",
      hardwareIdVersion: "",
      hardwareIdQuality: "",
    });
  }
});

test("legacy IP recovery ignores identity-less ghosts when one canonical owner exists", () => {
  const canonical = { id: "real", steamId: "76561198111191138", epicId: "", installId: "" };
  const ghost = { id: "ghost", steamId: "", epicId: "", installId: "" };
  assert.equal(chooseUnambiguousLegacyIpCandidate([ghost, canonical]), canonical);
});

test("legacy IP recovery refuses a shared IP with multiple canonical owners", () => {
  const first = { id: "first", steamId: "76561198111191138" };
  const second = { id: "second", steamId: "76561198111191139" };
  assert.equal(chooseUnambiguousLegacyIpCandidate([first, second]), null);
});

test("legacy IP recovery allows one historical identity-less archive profile", () => {
  const only = { id: "archive", steamId: "", epicId: "", installId: "" };
  assert.equal(chooseUnambiguousLegacyIpCandidate([only]), only);
  assert.equal(chooseUnambiguousLegacyIpCandidate([]), null);
});
