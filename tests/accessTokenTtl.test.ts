import assert from "node:assert/strict";
import test from "node:test";
import jwt from "jsonwebtoken";
import { accessTokenExpiresIn, accessTokenTtlProblem } from "../src/env/env";

test("an empty ACCESS_TOKEN_TTL means session tokens never expire", () => {
  assert.equal(accessTokenTtlProblem(""), null);
  assert.equal(accessTokenExpiresIn(""), undefined);
  const token = jwt.sign({ id: "p" }, "x".repeat(32), {});
  assert.equal((jwt.decode(token) as any).exp, undefined);
});

test("seconds and unit lifetimes are accepted, and a bare number is seconds, not milliseconds", () => {
  for (const ok of ["120", "86400", "2m", "24h", "7d", "30s"]) assert.equal(accessTokenTtlProblem(ok), null, ok);
  assert.equal(accessTokenExpiresIn("120"), 120);
  assert.equal(accessTokenExpiresIn("24h"), "24h");
  const token = jwt.sign({ id: "p" }, "x".repeat(32), { expiresIn: accessTokenExpiresIn("120") as number });
  const { iat, exp } = jwt.decode(token) as any;
  assert.equal(exp - iat, 120);
});

test("anything else stops the server instead of failing every login", () => {
  for (const bad of ["0", "-5", "2 m", "1w", "12.5h", "forever", "2mm", " 24h"]) {
    assert.notEqual(accessTokenTtlProblem(bad), null, bad);
  }
});
