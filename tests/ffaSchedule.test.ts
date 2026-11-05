import assert from "node:assert/strict";
import test from "node:test";
import { isFfaQueueOpen } from "../src/services/ffaSchedule";

const open = (iso: string) => isFfaQueueOpen(new Date(iso), true);

test("weekend FFA opens Friday 12:00am and closes Sunday 11:59pm Eastern (EDT)", () => {
  assert.equal(open("2026-09-25T03:59:00Z"), false); // Thu 11:59pm EDT
  assert.equal(open("2026-09-25T04:00:00Z"), true);  // Fri 12:00am EDT
  assert.equal(open("2026-09-26T16:00:00Z"), true);  // Sat noon
  assert.equal(open("2026-09-28T03:58:00Z"), true);  // Sun 11:58pm EDT
  assert.equal(open("2026-09-28T03:59:00Z"), false); // Sun 11:59pm EDT
  assert.equal(open("2026-09-29T16:00:00Z"), false); // Tue
});

test("the window follows Eastern standard time in winter (EST)", () => {
  assert.equal(open("2026-12-04T04:59:00Z"), false); // Thu 11:59pm EST
  assert.equal(open("2026-12-04T05:00:00Z"), true);  // Fri 12:00am EST
  assert.equal(open("2026-12-07T04:58:00Z"), true);  // Sun 11:58pm EST
  assert.equal(open("2026-12-07T04:59:00Z"), false); // Sun 11:59pm EST
});

test("with FFA_WEEKEND_ONLY off the queue is always open", () => {
  assert.equal(isFfaQueueOpen(new Date("2026-09-29T16:00:00Z"), false), true);
});
