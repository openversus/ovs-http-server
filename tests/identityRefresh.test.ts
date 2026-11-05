import assert from "node:assert/strict";
import test from "node:test";
import jwt from "jsonwebtoken";
import { redisClient } from "../src/config/redis";
import { SECRET } from "../src/middleware/auth";
import { refreshIpIdentityFromToken } from "../src/services/identityService";

const INSTALL = "0123456789abcdef0123456789abcdef";
const IP = "203.0.113.9";

// In-memory stand-in for the identity:<ip> hashes, the identity indexes and the IP's
// live sessions (active_ip_accounts:<ip>).
function fakeRedis(
  t: any,
  hashes: Record<string, Record<string, string>>,
  { active = [] as string[], indexes = {} as Record<string, string> } = {},
) {
  t.mock.method(redisClient, "zRemRangeByScore", async () => 0);
  t.mock.method(redisClient, "zRange", async () => active);
  t.mock.method(redisClient, "get", async (key: string) => indexes[key] ?? null);
  t.mock.method(redisClient, "exists", async (key: string) => (hashes[key] ? 1 : 0));
  t.mock.method(redisClient, "hSet", async (key: string, fields: Record<string, string>) => {
    hashes[key] = { ...(hashes[key] ?? {}), ...fields };
    return 1;
  });
  t.mock.method(redisClient, "expire", async () => true);
  t.mock.method(redisClient, "renameNX", async (from: string, to: string) => {
    if (hashes[to]) return false;
    hashes[to] = hashes[from];
    delete hashes[from];
    return true;
  });
  t.mock.method(redisClient, "del", async (key: string) => {
    delete hashes[key];
    return 1;
  });
}

// The claims /api/identify signs; the account id is empty when it was not known yet.
const claims = (overrides: Record<string, string> = {}) => ({
  id: "",
  steamId: "76561198000000001",
  epicId: "",
  installId: INSTALL,
  hardwareId: "",
  hardwareIdVersion: "",
  hardwareIdQuality: "",
  clientVersion: "2026.09.27.2",
  identityRegistered: "1",
  ...overrides,
});

const request = (token?: string) => ({ headers: token ? { "x-hydra-access-token": token } : {} }) as any;

test("an expired IP identity is rewritten from the client's verified token", async (t) => {
  const hashes: Record<string, Record<string, string>> = {};
  fakeRedis(t, hashes);

  const written = await refreshIpIdentityFromToken(request(jwt.sign(claims(), SECRET)), IP);
  assert.equal(written?.clientVersion, "2026.09.27.2");
  assert.deepEqual(
    { ...hashes[`identity:${IP}`] },
    {
      steamId: "76561198000000001",
      epicId: "",
      hardwareId: "",
      hardwareIdVersion: "",
      hardwareIdQuality: "",
      installId: INSTALL,
      clientVersion: "2026.09.27.2",
      identityRegistered: "1",
    },
  );
});

test("a live IP identity is left alone", async (t) => {
  const hashes: Record<string, Record<string, string>> = { [`identity:${IP}`]: { clientVersion: "fresh" } };
  fakeRedis(t, hashes);

  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims(), SECRET)), IP), null);
  assert.equal(hashes[`identity:${IP}`].clientVersion, "fresh");
});

test("only a verified token for a registered client refreshes anything", async (t) => {
  const hashes: Record<string, Record<string, string>> = {};
  fakeRedis(t, hashes);

  assert.equal(await refreshIpIdentityFromToken(request(), IP), null);
  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims(), "not the server's secret")), IP), null);
  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims({ identityRegistered: "" }), SECRET)), IP), null);
  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims({ clientVersion: "" }), SECRET)), IP), null);
  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims(), SECRET)), ""), null);
  assert.deepEqual(hashes, {});
});

test("in a household the record is only kept alive while this player is the only one playing", async (t) => {
  const token = jwt.sign(claims(), SECRET);
  const indexes = { [`identity:install:${INSTALL}`]: "me" };

  // Alone on the IP (its own session, or nobody's yet after a restart): refreshed.
  for (const active of [[], ["me"]]) {
    const hashes: Record<string, Record<string, string>> = {};
    fakeRedis(t, hashes, { active, indexes });
    assert.ok(await refreshIpIdentityFromToken(request(token), IP));
    assert.ok(hashes[`identity:${IP}`]);
    t.mock.restoreAll();
  }

  // A housemate is playing too: their next login must not pick up this player's ids.
  const hashes: Record<string, Record<string, string>> = {};
  fakeRedis(t, hashes, { active: ["me", "housemate"], indexes });
  assert.equal(await refreshIpIdentityFromToken(request(token), IP), null);
  assert.deepEqual(hashes, {});
});

test("a token whose account isn't known yet refreshes only while nobody plays from the IP", async (t) => {
  const hashes: Record<string, Record<string, string>> = {};
  fakeRedis(t, hashes, { active: ["someone"] });
  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims(), SECRET)), IP), null);
  assert.deepEqual(hashes, {});
});

test("an /api/identify that lands during a refresh is never overwritten", async (t) => {
  const hashes: Record<string, Record<string, string>> = {};
  fakeRedis(t, hashes, { indexes: { [`identity:install:${INSTALL}`]: "me" } });
  // The refresh saw no record, then another device's identify wrote one before the refresh's write.
  t.mock.method(redisClient, "exists", async () => {
    hashes[`identity:${IP}`] = { installId: "f".repeat(32) };
    return 0;
  });

  assert.equal(await refreshIpIdentityFromToken(request(jwt.sign(claims(), SECRET)), IP), null);
  assert.deepEqual(hashes, { [`identity:${IP}`]: { installId: "f".repeat(32) } });
});
