import assert from "node:assert/strict";
import test from "node:test";
import { hasP2PHost, parseNodePort } from "../src/services/nodePort";

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

test("every match with a human who plays can run P2P, whatever its mode", () => {
  const human = { isBot: false }, bot = { isBot: true }, spectator = { isSpectator: true };
  assert.equal(hasP2PHost([human, human]), true);                  // 1v1
  assert.equal(hasP2PHost([human, human, human, human]), true);    // 2v2, FFA of four
  assert.equal(hasP2PHost([human, bot]), true);                    // one human vs a bot
  assert.equal(hasP2PHost([human, bot, human, bot]), true);        // two humans vs bots
  assert.equal(hasP2PHost([human, human, spectator]), true);       // with a spectator
  assert.equal(hasP2PHost([human, human, human, human, spectator, spectator, spectator, spectator]), true); // the most a game holds
  assert.equal(hasP2PHost([{}, {}]), true);                        // the ranked matchmaker's entries carry no isBot
  assert.equal(hasP2PHost([bot, bot]), false);                     // nobody to host
  assert.equal(hasP2PHost([spectator, bot]), false);
  assert.equal(hasP2PHost([]), false);
});
