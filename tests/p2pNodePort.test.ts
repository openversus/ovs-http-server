import assert from "node:assert/strict";
import test from "node:test";
import { parseNodePort } from "../src/services/nodePort";

test("a node port is a number or numeric string from 1 to 65535", () => {
  assert.equal(parseNodePort(41234), 41234);
  assert.equal(parseNodePort("41234"), 41234);
  assert.equal(parseNodePort(" 65535 "), 65535);
  assert.equal(parseNodePort(1), 1);
});

test("anything else is no port", () => {
  for (const bad of [0, "0", -1, 65536, "65536", 1.5, "4123x", "", null, undefined, {}, [], true, "1e3", "+5"]) {
    assert.equal(parseNodePort(bad), 0, `${JSON.stringify(bad)}`);
  }
});
