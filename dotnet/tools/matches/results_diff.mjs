// PUT /ssc/invoke/submit_end_of_match_stats on the TS server and the C# port (http: MatchResults; match flow:
// MatchResultStream and GameStats), scenario by scenario, with the real reports in the Hydra corpus (dotnet/local/
// hydra-corpus/req/*submit_end_of_match_stats.bin, their players renamed to the harness's): the answers, the Redis state
// after, and in Mongo the players' stats, ratings and the match's archive (decompressed) with their stored types. Run
// from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/results_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/results_diff.mjs diff <ts.json> <cs.json>
//
// The TS server must be PR #49's code as committed. The C# run needs the http service at <baseUrl> and the match flow on
// the same scratch stores (it records the stats from the stream). Every human reports, so both servers record the
// stats from the same report; a step waits 1.5 s for the TS server's fire-and-forget and the match flow.
//
// Scratch stores, wiped before every step: REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET (as the other harnesses);
// REF_CORPUS: the corpus folder (default dotnet/local/hydra-corpus/req).
//
// Whole numbers the game sends as doubles ({_hydra_double: n}) are sent as plain numbers in the corpus steps (TS skipped
// the wrapped ones: GameStats); one step sends one wrapped and asserts what C# counts and TS did not.
import fs from "node:fs";
import { require, need, openScratch, state } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { EJSON } = require(process.cwd() + "/node_modules/bson");
const zstd = require(process.cwd() + "/node_modules/zstd-napi");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const oid = (n) => "00000000000000000014" + String(n).padStart(4, "0");
const [P1, P2, SPEC] = [1, 2, 3].map(oid);
const MATCH = oid(100), SET = oid(101);
const IP = "198.51.100.8";
const CORPUS = process.env.REF_CORPUS ?? "dotnet/local/hydra-corpus/req";
const token = (pid) => jwt.sign({ id: pid, profile_id: oid(900), wb_network_id: pid, hydraUsername: "OpenVersus_1", username: "Player1", current_ip: IP }, need("REF_JWT_SECRET"));

// Decoded report with its two players renamed P1, P2 (in the order they appear), whole doubles unwrapped unless asked.
function report(file, { keepWrapped = false } = {}) {
  const body = new HydraDecoder(fs.readFileSync(file)).readValue();
  const ids = Object.keys(body.EndOfMatchStats.PlayerMissionUpdates);
  let text = JSON.stringify(body);
  ids.forEach((id, i) => { text = text.replaceAll(id, [P1, P2][i]); });
  const out = JSON.parse(text);
  out.ContainerMatchId = MATCH;
  const unwrap = (v) => (Array.isArray(v) ? v.map(unwrap) : v && typeof v === "object"
    ? (Object.keys(v).length === 1 && typeof v._hydra_double === "number" && !keepWrapped ? v._hydra_double : Object.fromEntries(Object.entries(v).map(([k, x]) => [k, unwrap(x)])))
    : v);
  return unwrap(out);
}

async function call(baseUrl, pid, body) {
  const headers = { "x-real-ip": IP, "content-type": "application/x-ag-binary" };
  if (pid) headers["x-hydra-access-token"] = token(pid);
  const encoder = new HydraEncoder();
  encoder.encodeValue(body);
  try {
    const response = await fetch(`${baseUrl}/ssc/invoke/submit_end_of_match_stats`, { method: "PUT", headers, body: encoder.returnValue(), signal: AbortSignal.timeout(10000) });
    const bytes = Buffer.from(await response.arrayBuffer());
    let decoded;
    try { decoded = new HydraDecoder(bytes).readValue(); } catch { decoded = bytes.toString("utf8"); }
    return { status: response.status, body: decoded };
  } catch (e) {
    return { status: `<no answer: ${e.name}>` };
  }
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("results_diff");
  const seed = async ({ custom = false, spectator = false } = {}) => {
    const players = [{ playerId: P1, playerIndex: 0, teamIndex: 0, isHost: true, ip: IP, isBot: false }, { playerId: P2, playerIndex: 1, teamIndex: 1, isHost: false, ip: IP, isBot: false }];
    if (spectator) players.push({ playerId: SPEC, playerIndex: 8888, teamIndex: -1, isHost: false, ip: IP, isSpectator: true });
    await redis.set(MATCH, JSON.stringify({ players, matchId: MATCH, matchKey: "k", map: "M001", mode: "1v1", rollbackPort: 57003, p2p: false, ...(custom ? { isCustomGame: true } : {}) }), { EX: 1200 });
    await redis.set(`match_characters:${MATCH}`, JSON.stringify({ [P1]: "character_jake", [P2]: "character_finn" }), { EX: 1200 });
    if (!custom) {
      await redis.set(`ranked_set:${SET}`, JSON.stringify({ players, mode: "1v1", gamesPlayed: 1, scores: [0, 0], checkins: [] }), { EX: 600 });
      for (const p of [P1, P2]) await redis.set(`player_ranked_set:${p}`, SET, { EX: 600 });
    }
    for (const [i, p] of [P1, P2, SPEC].entries()) await redis.hSet(`connections:${p}`, { id: p, username: `Player${i + 1}`, character: ["character_jake", "character_finn", "character_shaggy"][i] });
  };

  const steps = [];
  async function step(name, setup, calls, waitMs = 1500) {
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    await setup?.();
    const started = Date.now();
    const answers = [];
    for (const [pid, body] of calls) answers.push(await call(baseUrl, pid, body));
    await sleep(waitMs);
    const archives = await db.collection("match_archives").find({}, { promoteValues: false }).toArray();
    steps.push(normalize({
      name,
      answers,
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^ovs:instance/.test(k))),
      mongo: Object.fromEntries(await Promise.all(["playerstats", "eloratings"].map(async (c) => [c,
        JSON.parse(EJSON.stringify(await db.collection(c).find({}, { promoteValues: false, sort: { account_id: 1 } }).toArray(), { relaxed: false }))]))),
      archives: archives.map((a) => ({
        doc: JSON.parse(EJSON.stringify({ ...a, compressed_data: undefined }, { relaxed: false })),
        types: Object.fromEntries(Object.entries(a).map(([k, v]) => [k, v?._bsontype ?? typeof v])),
        json: JSON.parse(zstd.decompress(Buffer.from(a.compressed_data.buffer)).toString("utf8")),
      })),
    }, started));
    process.stdout.write(`${name}: ${answers.map((a) => a.status).join(" ")}\n`);
  }

  const files = fs.readdirSync(CORPUS).filter((f) => f.includes("submit_end_of_match_stats")).sort();
  for (const file of files) {
    const body = report(`${CORPUS}/${file}`);
    await step(`corpus-${file.match(/__(\d+)__/)?.[1] ?? file}`, () => seed(), [[P1, body], [P2, body]]);
  }
  const first = report(`${CORPUS}/${files[0]}`);
  // TS took the first report, a spectator's included (TS-FINDINGS 4); C# takes a player's that agrees.
  const spectated = JSON.parse(JSON.stringify(first));
  spectated.EndOfMatchStats.PlayerMissionUpdates[P1]["Stat:Game:Character:TotalRingouts"] = 9;
  await step("custom-spectator-reports-first", () => seed({ custom: true, spectator: true }), [[SPEC, spectated], [P1, first], [P2, first]]);
  // One whole double sent as such: C# counts it, TS skipped it.
  const wrapped = report(`${CORPUS}/${files[0]}`);
  wrapped.EndOfMatchStats.PlayerMissionUpdates[P1]["Stat:Game:Character:TotalKnockbackAdded"] = { _hydra_double: 900 };
  await step("wrapped-whole-double", () => seed(), [[P1, wrapped], [P2, wrapped]]);
  await step("no-session", () => seed(), [[null, first]]);
  // The other player never reports: C# records the stats 30 s after the first report (the sweep), TS at once.
  await step("one-report-only", () => seed(), [[P1, first]], 33000);

  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function normalize(step, started) {
  const ids = new Map();
  const near = (n) => Math.abs(n - started) < 120000;
  const rename = (s) => s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/\b1\d{12}(\.\d+)?\b/g, (n) => (near(Number(n)) ? "<now ms>" : n))
    .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z/g, (d) => (near(Date.parse(d)) ? "<now iso>" : d));
  const walk = (v) => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x)]));
    if (typeof v === "number" && near(v)) return "<now ms>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// ── Deliberate differences: what must hold, then everything else must match ──
const clone = (v) => JSON.parse(JSON.stringify(v));
// Every step: the answer's Season (Season:Current), the C#-only keys (the reports, the stats' due time, the stream; the
// stats claim key TS sets from the first report and C# when it records), and ranked_set_score's value (TS "1", C# the
// team it counted).
function common(ts, cs) {
  const problems = [];
  const t = clone(ts), c = clone(cs);
  for (const [a, b] of t.answers.map((x, i) => [x, c.answers[i]])) {
    if (a?.body?.body?.Season !== undefined || b?.body?.body?.Season !== undefined) {
      if (a.body.body.Season !== "Season:SeasonFive" || b.body.body.Season !== "Season:SeasonSix") problems.push(`season ${a.body.body.Season} / ${b.body.body.Season}`);
      a.body.body.Season = b.body.body.Season = "<season>";
    }
  }
  for (const key of Object.keys(c.state)) if (/^match_results:|^match:results$/.test(key)) delete c.state[key];
  if (Object.keys(t.state).some((k) => /^match_results:|^match:results$/.test(k))) problems.push("TS wrote match_results");
  const score = `ranked_set_score:${SET}:${MATCH}`;
  if (t.state[score] || c.state[score]) {
    if (t.state[score]?.value !== "1" || !["0", "1"].includes(c.state[score]?.value)) problems.push(`set score key ${t.state[score]?.value} / ${c.state[score]?.value}`);
    t.state[score].value = c.state[score].value = "<counted>";
  }
  return { t, c, problems };
}
const EXPECTED = {
  "custom-spectator-reports-first": {
    why: "TS recorded the stats from the first report, the spectator's; C# from the first player's that agrees",
    check: (t, c) => {
      const ringouts = (run) => run.mongo.playerstats.find((d) => d.account_id === P1)?.characters_1v1?.character_jake?.ringouts?.$numberInt;
      const ok = ringouts(t) === "9" && ringouts(c) !== "9" && t.archives[0]?.json.mission_updates[P1]["Stat:Game:Character:TotalRingouts"] === 9;
      return { ok, strip: (run) => { run.mongo.playerstats = "<stats>"; run.archives = "<archive>"; } };
    },
  },
  "one-report-only": {
    why: "one human never reports: TS recorded the stats (and claimed game_stats_recorded) at once, C# 30 s after the first report; the stats the same",
    check: (t, c) => {
      const key = `game_stats_recorded:${MATCH}`;
      const ok = t.state[key]?.ttl === "~9m" && c.state[key]?.ttl === "~10m" && c.mongo.playerstats.length === 2;
      return { ok, strip: (run) => { run.state[key].ttl = "<claimed>"; } };
    },
  },
  "wrapped-whole-double": {
    why: "a whole number sent as a double: C# counts it (aggregate.totalKnockbackAdded 900), TS skipped it",
    check: (t, c) => {
      const agg = (run) => run.mongo.playerstats.find((d) => d.account_id === P1)?.aggregate ?? {};
      const before = Number(agg(t).totalKnockbackAdded?.$numberInt ?? 0), after = Number(agg(c).totalKnockbackAdded?.$numberInt ?? 0);
      return { ok: after - before === 900, strip: (run) => { delete run.mongo.playerstats.find((d) => d.account_id === P1).aggregate.totalKnockbackAdded; } };
    },
  },
};

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const name = a.steps[i]?.name ?? b.steps[i]?.name;
    const { t, c, problems } = common(a.steps[i], b.steps[i]);
    const expected = EXPECTED[name];
    if (expected) {
      const { ok, strip } = expected.check(t, c);
      if (!ok) problems.push(`NOT the expected difference (${expected.why})`);
      strip(t); strip(c);
    }
    const parts = ["answers", "state", "mongo", "archives"].filter((p) => JSON.stringify(t[p]) !== JSON.stringify(c[p]));
    if (!parts.length && !problems.length) {
      if (expected) console.log(`${name}: the expected difference (${expected.why})`);
      continue;
    }
    differing++;
    console.log(`${name}: ${problems.join("; ")}${parts.length ? ` differs in ${parts.join(", ")}` : ""}`);
    for (const p of parts) {
      console.log(`  ${p} A: ${JSON.stringify(t[p]).slice(0, 1500)}`);
      console.log(`  ${p} B: ${JSON.stringify(c[p]).slice(0, 1500)}`);
    }
  }
  console.log(differing ? `${differing} step(s) differ` : `no differences (${a.steps.length} steps)`);
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: results_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
