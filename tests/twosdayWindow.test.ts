import assert from "node:assert/strict";
import test from "node:test";
import { DEFAULT_TWOSDAY_WINDOW, isInTwosdayWindow, secondsOfDay } from "../src/utils/twosdayWindow";

const at = (iso: string) => isInTwosdayWindow(new Date(iso), DEFAULT_TWOSDAY_WINDOW);

test("Tuesday 3 PM to midnight on the Eastern clock, while daylight saving is on (EDT, UTC-4)", () => {
  // Tuesday 2026-10-06.
  assert.equal(at("2026-10-06T18:59:59Z"), false); // 2:59:59 PM EDT
  assert.equal(at("2026-10-06T19:00:00Z"), true); // 3:00 PM EDT
  assert.equal(at("2026-10-07T03:59:59Z"), true); // 11:59:59 PM EDT, Wednesday already in UTC
  assert.equal(at("2026-10-07T04:00:00Z"), false); // midnight EDT: Wednesday
});

test("the same wall clock in winter (EST, UTC-5)", () => {
  // Tuesday 2026-12-01.
  assert.equal(at("2026-12-01T19:30:00Z"), false); // 2:30 PM EST
  assert.equal(at("2026-12-01T20:00:00Z"), true); // 3:00 PM EST
  assert.equal(at("2026-12-02T04:59:59Z"), true); // 11:59:59 PM EST
  assert.equal(at("2026-12-02T05:00:00Z"), false);
});

test("never on another day", () => {
  assert.equal(at("2026-10-05T20:00:00Z"), false); // Monday 4 PM EDT
  assert.equal(at("2026-10-07T20:00:00Z"), false); // Wednesday 4 PM EDT
});

test("other hours, day and zone", () => {
  const window = { day: 6, start: "09:30", end: "10:00:30", timeZone: "Europe/London" };
  // Saturday 2026-10-10, BST (UTC+1).
  assert.equal(isInTwosdayWindow(new Date("2026-10-10T08:29:59Z"), window), false);
  assert.equal(isInTwosdayWindow(new Date("2026-10-10T08:30:00Z"), window), true);
  assert.equal(isInTwosdayWindow(new Date("2026-10-10T09:00:29Z"), window), true);
  assert.equal(isInTwosdayWindow(new Date("2026-10-10T09:00:30Z"), window), false);
});

test("a setting it cannot read throws, so the caller can treat it as off", () => {
  const now = new Date("2026-10-06T20:00:00Z");
  assert.throws(() => isInTwosdayWindow(now, { ...DEFAULT_TWOSDAY_WINDOW, start: "3pm" }));
  assert.throws(() => isInTwosdayWindow(now, { ...DEFAULT_TWOSDAY_WINDOW, start: "16:00", end: "15:00" }));
  assert.throws(() => isInTwosdayWindow(now, { ...DEFAULT_TWOSDAY_WINDOW, day: 7 }));
  assert.throws(() => isInTwosdayWindow(now, { ...DEFAULT_TWOSDAY_WINDOW, timeZone: "Eastern/Nowhere" }));
});

test("times of day", () => {
  assert.equal(secondsOfDay("15:00"), 15 * 3600);
  assert.equal(secondsOfDay("24:00"), 86400);
  assert.equal(secondsOfDay("23:59:59"), 86399);
  assert.equal(secondsOfDay("24:01"), null);
  assert.equal(secondsOfDay("12:60"), null);
});
