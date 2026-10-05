import assert from "node:assert/strict";
import test from "node:test";
import { redisClient } from "../src/config/redis";
import { awardFfaMatchXp, awardRankedSetXp, RANKED_SET_XP_CHANNEL } from "../src/services/rankedSetXpService";

// The TS server decides who a ranked set (or a public FFA game) pays and whether they won; the C# match flow pays it
// (RankedSetXpSubscriber: the battle pass 300 + 150 for a win, the account and character levels 400 + 200, once per
// setKey and player). These check what is published.
function harness(t: any) {
  const published: any[] = [];
  t.mock.method(redisClient, "publish", async (channel: string, message: string) => {
    assert.equal(channel, RANKED_SET_XP_CHANNEL);
    published.push(JSON.parse(message));
    return 1;
  });
  return { published, paid: () => published.map(p => [p.playerId, p.won, p.character, p.setKey]) };
}

const set = (overrides: Partial<Parameters<typeof awardRankedSetXp>[0]> = {}) => ({
  winnerIds: ["winner"],
  loserIds: ["loser"],
  playerCharacters: new Map([["winner", "character_shaggy"], ["loser", "character_BananaGuard"]]),
  setKey: "set-1",
  setScore: [2, 0] as [number, number],
  isConcede: false,
  isPregameDodge: false,
  ...overrides,
});

test("a completed ranked set pays each player once, as winner or loser, on the character they played", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set());
  assert.deepEqual(h.paid(), [
    ["winner", true, "character_shaggy", "ranked:set-1"],
    ["loser", false, "character_BananaGuard", "ranked:set-1"],
  ]);
});

test("pregame dodges pay nobody; a concede before any game pays only the side that stayed", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "dodge", isPregameDodge: true, setScore: [0, 0] }));
  assert.equal(h.published.length, 0);
  await awardRankedSetXp(set({ setKey: "concede", isConcede: true, setScore: [0, 0] }));
  assert.deepEqual(h.paid(), [["winner", true, "character_shaggy", "ranked:concede"]]);
});

test("a loser who walks out after playing (recorded as a concede) is still paid", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "walkout", isConcede: true, setScore: [2, 0] }));
  assert.deepEqual(h.paid().map(p => p[0]), ["winner", "loser"]);
});

test("an unmapped character is still sent (C# pays the battle pass and account level)", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "unmapped", loserIds: [], playerCharacters: new Map([["winner", "character_unknown"]]) }));
  assert.deepEqual(h.paid(), [["winner", true, "character_unknown", "ranked:unmapped"]]);
});

test("public FFA pays per game, keyed by the match", async (t) => {
  const h = harness(t);
  await awardFfaMatchXp("winner", true, "character_shaggy", "ffa-1");
  await awardFfaMatchXp("other", false, "character_BananaGuard", "ffa-1");
  assert.deepEqual(h.paid(), [
    ["winner", true, "character_shaggy", "ffa:ffa-1"],
    ["other", false, "character_BananaGuard", "ffa:ffa-1"],
  ]);
});
