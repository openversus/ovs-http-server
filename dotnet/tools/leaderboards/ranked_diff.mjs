// GET /ssc/invoke/ranked_data on the TS server and the C# port. Run from the repository root (it uses the TS server's
// node_modules):
//
//   node dotnet/tools/leaderboards/ranked_diff.mjs run <baseUrl> <out.json>      # scenarios on scratch stores
//   node dotnet/tools/leaderboards/ranked_diff.mjs diff <ts.json> <cs.json>
//   node dotnet/tools/leaderboards/ranked_diff.mjs replay <tsUrl> <csUrl>        # every stored rating, read only
//
// run: both servers on the same scratch stores, which it wipes first, and both with DEFAULT_ELO=1000 (a new rating's
// value, so it shows):
//   REF_REDIS_URL   a throwaway Redis, e.g. redis://default:pw@127.0.0.1:16390
//   REF_MONGO_URI   a scratch database (name containing ref/test/scratch; dropped)
//   REF_JWT_SECRET  the JWT secret both servers use
//   REF_PROFILE=1   record every Mongo command: `diff` compares the writes (a new rating, a username update)
// Scenarios: ratings with characters (per-character damage and ringouts, x.5 rounding, a missing field), games but no
// characters (BestCharacter keeps -1), no games, no rating (created), a changed username ($set), a character entry that
// is not an object (the fallback answer), integer-like character keys (a JS object orders them first), the character
// from the session, from playertesters, or neither; a token with no id and one whose id is no ObjectId.
//
// replay: REF_MONGO_URI (the database both servers use) and REF_JWT_SECRET; every account in eloratings with a token
// carrying its stored username (so nothing is written), Hydra and JSON; the timestamps (now) set aside, the byte length
// still compared.
import { require, need, openScratch, readProfile, dump, writeRun, diff, toPlain } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { MongoClient, ObjectId } = require(process.cwd() + "/node_modules/mongodb");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const oid = (n) => "00000000000000000000" + n.toString(16).padStart(4, "0");
const PATH = "/ssc/invoke/ranked_data";

async function call(baseUrl, token, hydra, headers = {}) {
  const response = await fetch(baseUrl + PATH, {
    headers: { "x-hydra-access-token": token, ...(hydra ? { "content-type": "application/x-ag-binary" } : {}), ...headers },
    signal: AbortSignal.timeout(15000),
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const body = hydra ? (bytes.length ? toPlain(new HydraDecoder(bytes).readValue()) : null) : bytes.toString();
  // The JSON text as sent, the timestamps (now) masked: a parsed body is a JS object, which puts integer-like keys first
  // whatever order they came in, so only the text shows the order.
  const text = hydra ? undefined : body.replace(/"_hydra_unix_date":(\d{10})\b/g, (m, n) => (Math.abs(Number(n) * 1000 - Date.now()) < 600000 ? '"_hydra_unix_date":"<now>"' : m));
  return { status: response.status, bytes: bytes.length, body: hydra ? body : JSON.parse(body || "null"), text };
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("ranked_diff");
  const rating = (n, fields) => ({
    _id: new ObjectId(oid(1000 + n)), account_id: oid(n), username: `Player${n}`, elo_1v1: 0, elo_2v2: 0, wins_1v1: 0, losses_1v1: 0,
    wins_2v2: 0, losses_2v2: 0, win_streak_1v1: 0, win_streak_2v2: 0, updated_at: 1790000000000.5, __v: 0, ...fields,
  });
  await db.collection("eloratings").insertMany([
    rating(1, {
      elo_1v1: 1200, wins_1v1: 7, losses_1v1: 3, elo_2v2: 1100, wins_2v2: 2, losses_2v2: 2,
      characters_1v1: { character_shaggy: { elo: 1250, wins: 5, losses: 2, streak: 1 }, character_finn: { elo: 1300, wins: 2, losses: 1, streak: 0 } },
      characters_2v2: { character_taz: { elo: 1100, wins: 2, losses: 2, streak: 0 } },
    }),
    rating(2, { elo_1v1: 1111, wins_1v1: 3, losses_1v1: 1 }),
    rating(3, {}),
    rating(5, { username: "OldName", elo_1v1: 900, wins_1v1: 1 }),
    rating(6, { wins_1v1: 1, characters_1v1: { character_x: null } }),
    rating(7, { elo_1v1: 1500, wins_1v1: 20, losses_1v1: 1 }),
    rating(8, { elo_1v1: 1200, wins_1v1: 4, losses_1v1: 4 }), // ties with player 1; the later _id ranks below
    rating(9, { elo_1v1: 5000, wins_1v1: 0, losses_1v1: 0 }), // no games: not ranked
    // Stored as character_b, 10, 2 (a Map keeps that order; an object literal would already be in JS order 2, 10, character_b).
    // character_b and 10 tie: the first in JS order (10) is the best character, not the first stored.
    rating(11, { elo_1v1: 1000, wins_1v1: 2, characters_1v1: new Map([["character_b", { elo: 30, wins: 1, losses: 0 }], ["10", { elo: 30, wins: 1, losses: 0 }], ["2", { elo: 20, wins: 0, losses: 1 }]]) }),
  ]);
  await db.collection("playerstats").insertMany([
    { account_id: oid(1), characters_1v1: { character_shaggy: { totalDamageDealt: 1234.5, ringouts: 7 }, character_finn: { totalDamageDealt: 99.49 } }, characters_2v2: {} },
    { account_id: oid(2), characters_1v1: { character_arya: { totalDamageDealt: 10.5, ringouts: 1 } } },
    { account_id: oid(11), characters_1v1: { 2: { totalDamageDealt: 2.5, ringouts: 2 } } },
  ]);
  await db.collection("playertesters").insertMany([
    { _id: new ObjectId(oid(1)), name: "PlayerOne", character: "character_taz" },
    { _id: new ObjectId(oid(2)), name: "PlayerTwo", character: "character_arya" },
  ]);
  await redis.hSet(`connections:${oid(1)}`, { id: oid(1), character: "character_jake" });

  const secret = need("REF_JWT_SECRET");
  const token = (claims) => jwt.sign(claims, secret);
  const steps = [];
  const step = async (name, claims, hydra = false) => steps.push({ name, response: await call(baseUrl, token(claims), hydra), state: await dump(redis, db) });
  await step("characters", { id: oid(1), username: "Player1" });
  await step("characters-hydra", { id: oid(1), username: "Player1" }, true);
  await step("games-no-characters", { id: oid(2), username: "Player2" });
  await step("games-no-characters-hydra", { id: oid(2), username: "Player2" }, true);
  await step("no-games", { id: oid(3), username: "Player3" });
  await step("no-rating-created", { id: oid(4), username: "NewPlayer" });
  await step("no-rating-again", { id: oid(4), username: "NewPlayer" });
  await step("no-rating-no-username", { id: oid(12) });
  await step("username-changed", { id: oid(5), username: "NewName" });
  await step("username-same", { id: oid(5), username: "NewName" });
  await step("character-not-an-object", { id: oid(6), username: "Player6" });
  await step("integer-like-keys", { id: oid(11), username: "Player11" });
  await step("integer-like-keys-hydra", { id: oid(11), username: "Player11" }, true);
  await step("no-id", { username: "Nobody" });
  await step("id-not-an-objectid", { id: "not-an-objectid", username: "Odd" });
  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "ranked_diff"));
  await close();
}

async function replay(tsUrl, csUrl) {
  const mongo = new MongoClient(need("REF_MONGO_URI"));
  await mongo.connect();
  const ratings = await mongo.db().collection("eloratings").find({}, { projection: { account_id: 1, username: 1 } }).sort({ _id: 1 }).toArray();
  await mongo.close();
  const secret = need("REF_JWT_SECRET");
  const started = Date.now();
  // Timestamps within ten minutes of the run are "now": both answers stamp every entry with the time of the request.
  const now = (v) => {
    if (Array.isArray(v)) return v.map(now);
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && typeof v._hydra_unix_date === "number" && Math.abs(v._hydra_unix_date * 1000 - started) < 600000) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, now(x)]));
    }
    return v;
  };
  let answers = 0, ranked = 0;
  const problems = [];
  for (const r of ratings) {
    const token = jwt.sign({ id: r.account_id, username: r.username }, secret);
    for (const hydra of [false, true]) {
      const [ts, cs] = [await call(tsUrl, token, hydra), await call(csUrl, token, hydra)];
      answers++;
      if (!hydra && ts.body?.body?.SeasonalData?.["Season:SeasonFive"]?.Ranked?.DataByMode?.["1v1"]?.FinalLeaderboardRank > 0) ranked++;
      const same = ts.status === cs.status && ts.bytes === cs.bytes && JSON.stringify(now(ts.body)) === JSON.stringify(now(cs.body)) && ts.text === cs.text;
      if (!same) problems.push(`${r.account_id} ${hydra ? "hydra" : "json"}: ${ts.status}/${ts.bytes} vs ${cs.status}/${cs.bytes}`);
    }
  }
  console.log(`${answers} answers (${ratings.length} ratings x2 encodings; ${ranked} ranked in 1v1): ${problems.length} differences`);
  for (const p of problems.slice(0, 20)) console.log("  " + p);
  process.exitCode = problems.length ? 1 : 0;
}

const [, , command, a, b] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true });
else if (command === "replay" && a && b) await replay(a, b);
else {
  console.error("usage: ranked_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json> | replay <tsUrl> <csUrl>");
  process.exit(2);
}
