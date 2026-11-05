import assert from "node:assert/strict";
import test from "node:test";
import { redisClient, redisSetPlayerConnectionByIp, redisUpdateIpMirror } from "../src/config/redis";

// In-memory stand-in for the connections:<ip> hashes.
function fakeRedis(t: any, hashes: Record<string, Record<string, string>>) {
  const writes: string[] = [];
  t.mock.method(redisClient, "hGet", async (key: string, field: string) => hashes[key]?.[field] ?? null);
  t.mock.method(redisClient, "hSet", async (key: string, fields: Record<string, string>) => {
    writes.push(key);
    hashes[key] = { ...(hashes[key] ?? {}), ...fields };
    return 1;
  });
  return writes;
}

test("the IP mirror is only written when it belongs to the same account", async (t) => {
  const hashes = { "connections:203.0.113.9": { id: "alice", character: "character_shaggy" } };
  const writes = fakeRedis(t, hashes);
  assert.equal(await redisUpdateIpMirror("203.0.113.9", "alice", { character: "character_Jason" }), true);
  assert.equal(hashes["connections:203.0.113.9"].character, "character_Jason");
  // A household member on the same IP never writes into Alice's record.
  assert.equal(await redisUpdateIpMirror("203.0.113.9", "bob", { character: "character_batman" }), false);
  assert.equal(hashes["connections:203.0.113.9"].character, "character_Jason");
  // No mirror (expired) or no IP: nothing is created.
  assert.equal(await redisUpdateIpMirror("198.51.100.1", "alice", { character: "x" }), false);
  assert.equal(await redisUpdateIpMirror("", "alice", { character: "x" }), false);
  assert.deepEqual(writes, ["connections:203.0.113.9"]);
});

test("a full connection write to the IP slot is guarded the same way", async (t) => {
  const hashes: Record<string, Record<string, string>> = { "connections:203.0.113.9": { id: "alice" } };
  const writes = fakeRedis(t, hashes);
  await redisSetPlayerConnectionByIp("203.0.113.9", { id: "bob", lobby_id: "L1" } as any);
  assert.deepEqual(writes, []);
  await redisSetPlayerConnectionByIp("203.0.113.9", { id: "alice", lobby_id: "L2" } as any);
  assert.equal(hashes["connections:203.0.113.9"].lobby_id, "L2");
});
