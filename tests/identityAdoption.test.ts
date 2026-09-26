import assert from "node:assert/strict";
import test from "node:test";
import { chooseAdoptionCandidate, chooseUnambiguousLegacyIpCandidate } from "../src/services/identityNormalization";

const STEAM = "76561198000000001";
const INSTALL = "0123456789abcdef0123456789abcdef";
const day = (n: number) => new Date(Date.UTC(2026, 8, n));

const idLess = (id: string, lastSeen = day(1)) => ({ id, steamId: "", epicId: "", installId: "", provisional: false, lastSeenAt: lastSeen });
const provisional = (id: string, lastSeen = day(1)) => ({ ...idLess(id, lastSeen), provisional: true });
const steamOwner = (id: string) => ({ ...idLess(id), steamId: STEAM });

test("adoption takes the IP's single id-less account (e.g. an Internet Archive player updating)", () => {
  assert.equal(chooseAdoptionCandidate([idLess("archive")])?.id, "archive");
  // An account that already has a durable id is never taken over.
  assert.equal(chooseAdoptionCandidate([steamOwner("steam"), idLess("archive")])?.id, "archive");
  assert.equal(chooseAdoptionCandidate([steamOwner("steam")]), null);
  assert.equal(chooseAdoptionCandidate([{ ...idLess("installed"), installId: INSTALL }]), null);
});

test("adoption refuses a household with two id-less accounts (manual merge path)", () => {
  assert.equal(chooseAdoptionCandidate([idLess("a"), idLess("b")]), null);
});

test("adoption prefers a real legacy account, then falls back to the newest provisional one", () => {
  assert.equal(chooseAdoptionCandidate([provisional("p"), idLess("legacy")])?.id, "legacy");
  assert.equal(
    chooseAdoptionCandidate([provisional("old", day(1)), provisional("new", day(5))])?.id,
    "new",
  );
  assert.equal(chooseAdoptionCandidate([]), null);
});

test("IP recovery for identity-less logins never counts provisional accounts", () => {
  // Provisional accounts neither own the IP nor make it ambiguous.
  assert.equal(chooseUnambiguousLegacyIpCandidate([provisional("p1"), provisional("p2"), idLess("real")])?.id, "real");
  assert.equal(chooseUnambiguousLegacyIpCandidate([provisional("p1"), provisional("p2")]), null);
  assert.equal(chooseUnambiguousLegacyIpCandidate([provisional("p"), steamOwner("owner")])?.id, "owner");
});
