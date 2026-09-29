import assert from "node:assert/strict";
import test from "node:test";
import jwt from "jsonwebtoken";
import { jwtSecretProblem } from "../src/env/env";
import { hydraTokenMiddleware, SECRET } from "../src/middleware/auth";

test("JWT_SECRET must be long and never the old public value", () => {
  assert.match(jwtSecretProblem("SHHHH!!")!, /old public value/);
  assert.match(jwtSecretProblem("short-secret")!, /at least 32/);
  assert.equal(jwtSecretProblem("x".repeat(32)), null);
  // The server's own secret passed the same check at startup.
  assert.equal(jwtSecretProblem(SECRET), null);
});

test("a token signed with the old public secret is no longer accepted", () => {
  const forged = jwt.sign({ id: "someone-else" }, "SHHHH!!");
  assert.throws(() => jwt.verify(forged, SECRET));
});

function fakeResponse() {
  const res: any = { statusCode: 200, body: undefined as unknown, ended: false };
  res.status = (code: number) => { res.statusCode = code; return res; };
  res.json = (body: unknown) => { res.body = body; res.ended = true; return res; };
  return res;
}

test("a request without a token gets a 401 answer instead of hanging", () => {
  const res = fakeResponse();
  let nextCalled = false;
  hydraTokenMiddleware({ url: "/ssc/invoke/anything", hostname: "example.test", headers: {} } as any, res, () => { nextCalled = true; });
  assert.equal(nextCalled, false);
  assert.equal(res.statusCode, 401);
  assert.equal(res.ended, true);
});

const requestWithToken = (token: string) =>
  ({ url: "/ssc/invoke/anything", method: "POST", hostname: "example.test", headers: { "x-hydra-access-token": token } }) as any;

test("a token the server can't verify gets the same 401 instead of reaching the handlers", () => {
  for (const token of [jwt.sign({ id: "someone" }, "SHHHH!!"), "not-a-jwt"]) {
    const res = fakeResponse();
    let nextCalled = false;
    hydraTokenMiddleware(requestWithToken(token), res, () => { nextCalled = true; });
    assert.equal(nextCalled, false);
    assert.equal(res.statusCode, 401);
    assert.equal(res.ended, true);
  }
});

test("a valid token passes with its claims decoded", () => {
  const req = requestWithToken(jwt.sign({ id: "player" }, SECRET));
  let nextCalled = false;
  hydraTokenMiddleware(req, fakeResponse(), () => { nextCalled = true; });
  assert.equal(nextCalled, true);
  assert.equal(req.token.id, "player");
});
