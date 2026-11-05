import assert from "node:assert/strict";
import test from "node:test";
import { findPlayerById } from "../src/database/PlayerTester";

test("a malformed or empty account id finds no player instead of throwing", async () => {
  // findById would throw a CastError on these; some callers have no try/catch.
  assert.equal(await findPlayerById("not-an-object-id"), null);
  assert.equal(await findPlayerById(""), null);
  assert.equal(await findPlayerById(undefined), null);
});
