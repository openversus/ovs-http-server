import assert from "node:assert/strict";
import test from "node:test";
import { redisClient } from "../src/config/redis";
import env from "../src/env/env";
import { requireCurrentClientForGameplay } from "../src/services/clientUpdateGate";

// A gameplay request from the account "player" whose live session (connections:player) is `session`.
function run(t: any, session: Record<string, string>) {
  const published: string[] = [];
  t.mock.method(redisClient, "hGetAll", async (key: string) => (key === "connections:player" ? session : {}));
  t.mock.method(redisClient, "set", async () => "OK");
  t.mock.method(redisClient, "incr", async () => 1);
  t.mock.method(redisClient, "expire", async () => true);
  t.mock.method(redisClient, "publish", async (_channel: string, message: string) => {
    published.push(message);
    return 1;
  });

  const req = {
    token: { id: "player" },
    header: () => undefined,
    method: "POST",
    originalUrl: "/matches/matchmaking/1v1-retail/request",
    path: "/matches/matchmaking/1v1-retail/request",
  } as any;
  const res: any = { statusCode: 0, body: undefined };
  res.status = (code: number) => { res.statusCode = code; return res; };
  res.send = (body: unknown) => { res.body = body; return res; };

  let passed = false;
  return requireCurrentClientForGameplay(req, res, () => { passed = true; })
    .then(() => ({ passed, res, published }));
}

test("a registered, current client passes the gameplay gate", async (t) => {
  const { passed, published } = await run(t, { id: "player", identityRegistered: "1", clientVersion: "9999.1.1.1" });
  assert.equal(passed, true);
  assert.deepEqual(published, []);
});

test("a client that never registered through /api/identify is kept out and shown the update toast", async (t) => {
  if (!env.CLIENT_VERSION_CHECK) {
    t.skip("CLIENT_VERSION_CHECK is off in this .env");
    return;
  }
  const { passed, res, published } = await run(t, { id: "player", identityRegistered: "", clientVersion: "" });
  assert.equal(passed, false);
  // Hydra answers with HTTP 200 and return_code 1, so the old game doesn't retry forever.
  assert.equal(res.statusCode, 200);
  assert.equal(res.body.return_code, 1);
  assert.equal(res.body.body.ErrorCode, "ClientOutdated");
  assert.equal(published.length, 1);
  assert.equal(JSON.parse(published[0]).playerId, "player");
});
