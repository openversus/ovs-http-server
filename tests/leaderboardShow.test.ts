import assert from "node:assert/strict";
import test from "node:test";
import { HydraEncoder } from "mvs-dump";
import { EloRatingModel } from "../src/database/EloRating";
import { getLeaderboard } from "../src/services/eloService";
import { buildLeaderboardShowBody, parseLeaderboardSlug } from "../src/services/leaderboardShow";

const rows = [
  { rank: 1, account_id: "acct-a", username: "A", elo: 212, bestCharacter: "character_shaggy" },
  { rank: 2, account_id: "acct-b", username: "B", elo: 190, bestCharacter: "character_Jason" },
];

test("leaderboard slugs map to mode and optional character", () => {
  assert.deepEqual(parseLeaderboardSlug("ranked_season5_1v1_all"), { mode: "1v1", characterSlug: undefined });
  assert.deepEqual(parseLeaderboardSlug("ranked_season5_2v2_all"), { mode: "2v2", characterSlug: undefined });
  assert.deepEqual(parseLeaderboardSlug("ranked_season5_1v1_character_Jason"), { mode: "1v1", characterSlug: "character_Jason" });
});

test("show echoes exactly the fields the client asked for, in the Hydra leaders shape", () => {
  // Query the client sends when opening the 1v1 leaderboard (from the HTTP log).
  const query = {
    count: "100",
    account_fields: "data.__unused",
    fields: ["data.__unused", "server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.1v1.BestCharacter.CharacterSlug"],
    partial_response: "1",
  };
  const body = buildLeaderboardShowBody(rows, query) as any;
  assert.equal(body.leaders.length, 2);
  const [first] = body.leaders;
  assert.equal(first.rank, 1);
  assert.equal(first.score, 212);
  assert.equal(first.id, "acct-a");
  assert.equal(first.profile["server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.1v1.BestCharacter.CharacterSlug"], "character_shaggy");
  assert.ok("data.__unused" in first.profile);
  assert.ok("data.__unused" in first.account);
  assert.equal(first.account["identity.username"], "A");
  const encoder = new HydraEncoder();
  encoder.encodeValue(body);
  assert.ok(encoder.returnValue().length > 50);
});

test("around-me (no query) still returns rank, score and username per row", () => {
  const body = buildLeaderboardShowBody(rows) as any;
  assert.deepEqual(body.leaders.map((l: any) => [l.rank, l.score, l.account["identity.username"]]), [[1, 212, "A"], [2, 190, "B"]]);
});

test("a character leaderboard page starts where it was asked to (around-me window)", async (t) => {
  let pipeline: any[] = [];
  t.mock.method(EloRatingModel, "aggregate", async (stages: any[]) => {
    pipeline = stages;
    return [{ account_id: "acct-c", username: "C", charElo: 150, charWins: 3, charLosses: 1 }];
  });

  const page = await getLeaderboard("1v1", 11, "character_Jason", 20);
  assert.deepEqual(pipeline.find((stage) => "$skip" in stage), { $skip: 20 });
  assert.deepEqual(pipeline.find((stage) => "$sort" in stage), { $sort: { charElo: -1, _id: 1 } });
  assert.equal(page[0].rank, 21);
  assert.equal(page[0].bestCharacter, "character_Jason");
});
