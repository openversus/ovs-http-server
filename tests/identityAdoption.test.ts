import assert from "node:assert/strict";
import test from "node:test";
import { chooseAdoptionCandidate, chooseUnambiguousLegacyIpCandidate, idLessAccountFilter, IpIdentity, mergeIpIdentity, STALE_IP_LINK_DAYS, staleIpLinkFilter } from "../src/services/identityNormalization";

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

test("IP rule: releases only other, durable-id, 7+-day-inactive accounts on that IP", () => {
  const now = new Date("2026-09-26T12:00:00Z");
  const filter = staleIpLinkFilter("203.0.113.9", "me", now) as any;
  assert.equal(filter.ip, "203.0.113.9");
  assert.deepEqual(filter._id, { $ne: "me" });
  assert.equal(filter.lastSeenAt.$lt.toISOString(), "2026-09-19T12:00:00.000Z");
  assert.deepEqual(filter.provisional, { $ne: true });
  // Only accounts with a real Steam, Epic or install id match: an account with no
  // durable id (e.g. an Internet Archive player who hasn't updated) is never released.
  const matchesDurable = (field: string, value: string) =>
    filter.$or.some((clause: any) => field in clause && clause[field].test(value));
  assert.ok(matchesDurable("steamId", STEAM));
  assert.ok(matchesDurable("epicId", INSTALL));
  assert.ok(matchesDurable("installId", INSTALL));
  assert.ok(!matchesDurable("steamId", ""));
  assert.ok(!matchesDurable("installId", ""));
  assert.ok(!matchesDurable("steamId", "Unknown"));
  assert.equal(STALE_IP_LINK_DAYS, 7);
});

test("adoption queries only accounts without a durable id, so id-carrying ones can't hide them", () => {
  const legacy = idLessAccountFilter("203.0.113.9", false) as any;
  assert.equal(legacy.ip, "203.0.113.9");
  assert.deepEqual(legacy.provisional, { $ne: true });
  assert.equal((idLessAccountFilter("203.0.113.9", true) as any).provisional, true);
  const excluded = (field: string, value: string) =>
    legacy.$nor.some((clause: any) => field in clause && clause[field].test(value));
  assert.ok(excluded("steamId", STEAM));
  assert.ok(excluded("installId", INSTALL));
  assert.ok(!excluded("steamId", ""));
});

const ipIdentity = (overrides: Partial<IpIdentity> = {}): IpIdentity => ({
  steamId: "",
  epicId: "",
  installId: INSTALL,
  clientVersion: "2026.09.27.2",
  hardwareId: "",
  hardwareIdVersion: "",
  hardwareIdQuality: "",
  ...overrides,
});

test("a second identify from the same install adds to the IP record instead of blanking it", () => {
  const first = ipIdentity({ steamId: STEAM });
  // The second call arrives without the Steam id (e.g. the Steam API wasn't ready).
  assert.equal(mergeIpIdentity(first, ipIdentity()).steamId, STEAM);
  // New values still win.
  const other = "76561198000000002";
  assert.equal(mergeIpIdentity(first, ipIdentity({ steamId: other })).steamId, other);
  assert.equal(mergeIpIdentity(first, ipIdentity({ clientVersion: "2026.09.28.1" })).clientVersion, "2026.09.28.1");
});

test("an identify from another install replaces the IP record", () => {
  const first = ipIdentity({ steamId: STEAM });
  const otherDevice = ipIdentity({ installId: "f".repeat(32) });
  assert.deepEqual(mergeIpIdentity(first, otherDevice), otherDevice);
  // Without an install id nothing ties the two calls together.
  const noInstall = ipIdentity({ installId: "" });
  assert.deepEqual(mergeIpIdentity(first, noInstall), noInstall);
  assert.deepEqual(mergeIpIdentity(null, first), first);
});
