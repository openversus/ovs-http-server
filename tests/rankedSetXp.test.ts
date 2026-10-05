import assert from "node:assert/strict";
import test from "node:test";
import { redisClient } from "../src/config/redis";
import { PlayerRewardTrackStateModel } from "../src/database/PlayerRewardTrackStates";
import { PlayerCountersModel } from "../src/database/PlayerCounters";
import { awardFfaMatchXp, awardRankedSetXp } from "../src/services/rankedSetXpService";

// In-memory stand-ins for Redis (dedup keys, pub/sub) and the two Mongo models.
function harness(t: any) {
  const keys = new Set<string>();
  const published: any[] = [];
  const tracks = new Map<string, any>();
  const toasts = new Map<string, number>();
  t.mock.method(redisClient, "set", async (key: string, _v: string, opts?: any) => {
    if (opts?.NX && keys.has(key)) return null;
    keys.add(key);
    return "OK";
  });
  t.mock.method(redisClient, "publish", async (_channel: string, message: string) => {
    published.push(JSON.parse(message));
    return 1;
  });
  t.mock.method(PlayerRewardTrackStateModel, "findOneAndUpdate", (filter: any, update: any) => ({
    lean: async () => {
      const id = `${filter.accountId}:${filter.trackSlug}`;
      const doc = tracks.get(id) ?? { ...(update.$setOnInsert ?? {}), currentScore: 0 };
      if (update.$inc) doc.currentScore += update.$inc.currentScore;
      if (update.$set) Object.assign(doc, update.$set);
      tracks.set(id, doc);
      return { ...doc };
    },
  }) as any);
  t.mock.method(PlayerCountersModel, "findOneAndUpdate", (filter: any, update: any) => ({
    lean: async () => {
      const current = toasts.get(filter.accountId) ?? 100;
      const next = current + (update.$inc?.match_toasts ?? 0);
      toasts.set(filter.accountId, next);
      return { accountId: filter.accountId, match_toasts: next };
    },
  }) as any);
  return { published, tracks, toasts };
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

test("a completed ranked set pays battle pass + Fighter Pass once, on each player's own character", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set());
  await awardRankedSetXp(set()); // same set again: no double grant
  assert.equal(h.tracks.get("winner:mrt_battlepass_season_five").currentScore, 600);
  assert.equal(h.tracks.get("loser:mrt_battlepass_season_five").currentScore, 400);
  assert.equal(h.tracks.get("winner:mrt_mastery_shaggy").currentScore, 600);
  assert.equal(h.tracks.get("loser:mrt_mastery_banana_guard").currentScore, 400);
  // 600 crosses tiers 1-2 (10 + 10), 400 crosses tier 1 (10).
  assert.equal(h.toasts.get("winner"), 120);
  assert.equal(h.toasts.get("loser"), 110);
  assert.deepEqual(h.published.map(p => [p.accountId, p.trackSlug, p.rewardTrackClass]), [
    ["winner", "mrt_battlepass_season_five", "MvsBattlepassRewardTrackHsda"],
    ["winner", "mrt_mastery_shaggy", "MvsCharacterMasteryRewardTrackHsda"],
    ["loser", "mrt_battlepass_season_five", "MvsBattlepassRewardTrackHsda"],
    ["loser", "mrt_mastery_banana_guard", "MvsCharacterMasteryRewardTrackHsda"],
  ]);
});

test("pregame dodges pay nobody; a concede before any game pays only the side that stayed", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "dodge", isPregameDodge: true, setScore: [0, 0] }));
  assert.equal(h.tracks.size, 0);
  await awardRankedSetXp(set({ setKey: "concede", isConcede: true, setScore: [0, 0] }));
  assert.deepEqual([...h.tracks.keys()], ["winner:mrt_battlepass_season_five", "winner:mrt_mastery_shaggy"]);
});

test("a loser who walks out after playing (recorded as a concede) is still paid", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "walkout", isConcede: true, setScore: [2, 0] }));
  assert.equal(h.tracks.get("loser:mrt_battlepass_season_five").currentScore, 400);
  assert.equal(h.tracks.get("loser:mrt_mastery_banana_guard").currentScore, 400);
});

test("crossing Fighter Pass tier 5 feeds the battle pass", async (t) => {
  const h = harness(t);
  // 2,500 XP, then a won set (+600) crosses tier 5 at 3,000.
  h.tracks.set("winner:mrt_mastery_shaggy", { currentScore: 2_500 });
  await awardRankedSetXp(set({ setKey: "tier5", loserIds: [] }));
  assert.equal(h.tracks.get("winner:mrt_mastery_shaggy").currentScore, 3_100);
  // 600 for winning the set + 600 from Fighter Pass tier 5.
  assert.equal(h.tracks.get("winner:mrt_battlepass_season_five").currentScore, 1_200);
});

test("an unmapped character still earns battle pass XP for the set", async (t) => {
  const h = harness(t);
  await awardRankedSetXp(set({ setKey: "unmapped", loserIds: [], playerCharacters: new Map([["winner", "character_unknown"]]) }));
  assert.deepEqual([...h.tracks.keys()], ["winner:mrt_battlepass_season_five"]);
});

test("public FFA pays per game: 600 to the winner, 400 to the rest, once per match", async (t) => {
  const h = harness(t);
  await awardFfaMatchXp("winner", true, "character_shaggy", "ffa-1");
  await awardFfaMatchXp("winner", true, "character_shaggy", "ffa-1"); // resubmitted stats
  await awardFfaMatchXp("other", false, "character_BananaGuard", "ffa-1");
  assert.equal(h.tracks.get("winner:mrt_battlepass_season_five").currentScore, 600);
  assert.equal(h.tracks.get("winner:mrt_mastery_shaggy").currentScore, 600);
  assert.equal(h.tracks.get("other:mrt_battlepass_season_five").currentScore, 400);
  assert.equal(h.tracks.get("other:mrt_mastery_banana_guard").currentScore, 400);
});
