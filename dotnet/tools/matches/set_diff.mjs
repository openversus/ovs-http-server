// A ranked set between its games (PUT /ssc/invoke/match_set_checkin, match_set_absent, match_set_concede,
// faceoff_timeout) on the TS server and the C# port (match flow, RankedSets and SetRatings), scenario by scenario: the
// answers, every Redis write and publish the server made (MONITOR; writes compared as a set, publishes in order), the
// Redis state after, and the ratings and set stats in Mongo with their stored types (eloratings, playerstats). Run from
// the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/set_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/set_diff.mjs diff <ts.json> <cs.json>
//
// The TS server must be PR #49's code as committed, never a working tree with the bench patch (that one changes
// createNextSetMatch's port).
//
// Scratch stores, wiped before every step (never point these at data you want to keep):
//   REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET  as for the other harnesses
//   REF_SNAPSHOT_URI  optional: a copy of prod's Mongo (read only). Its ratings and set stats, under the harness's ids
//                     and names, are rated through whole sets ("replay" steps): real characters maps, streaks and counts.
// No websocket is needed: what the TS websocket would do with the publishes is not this harness's.
//
// The lock (ranked_set_lock:{set}) is compared apart from the writes: TS sets "1" and deletes it, C# sets a token and
// deletes it with a script, and C# takes it before its first write instead of after the check-in. Both must take it and
// leave it released.
import fs from "node:fs";
import { require, need, openScratch, openMonitor, writes, state } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { MongoClient, ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const { EJSON } = require(process.cwd() + "/node_modules/bson");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const oid = (n) => "00000000000000000012" + String(n).padStart(4, "0");
const [P1, P2, P3, P4] = [1, 2, 3, 4].map(oid);
const PLAYERS = [P1, P2, P3, P4];
const SET = oid(100);
const IP = "198.51.100.8";
const CHANNELS = new Set(["ranked_set:checkin", "ranked_set:leaver", "ranked_set:fullrankupdate", "match:notifications", "matchmaking:complete"]);
const token = (pid) => jwt.sign({ id: pid, profile_id: oid(900 + PLAYERS.indexOf(pid)), wb_network_id: pid, hydraUsername: `OpenVersus_${PLAYERS.indexOf(pid) + 1}`, username: `Player${PLAYERS.indexOf(pid) + 1}`, current_ip: IP }, need("REF_JWT_SECRET"));

async function call(baseUrl, route, pid, body) {
  const headers = { "x-real-ip": IP };
  if (pid) headers["x-hydra-access-token"] = token(pid);
  let payload;
  if (body !== undefined) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
    headers["content-type"] = "application/x-ag-binary";
  }
  let response, bytes;
  try {
    response = await fetch(`${baseUrl}/ssc/invoke/${route}`, { method: "PUT", headers, body: payload, signal: AbortSignal.timeout(20000) });
    bytes = Buffer.from(await response.arrayBuffer());
  } catch (e) {
    return { status: `<no answer: ${e.name}>` };
  }
  let decoded;
  try {
    decoded = new HydraDecoder(bytes).readValue();
  } catch {
    try { decoded = JSON.parse(bytes.toString("utf8")); } catch { decoded = bytes.toString("utf8"); }
  }
  return { status: response.status, type: response.headers.get("content-type"), body: decoded };
}

const team = (pid, teamIndex, playerIndex, extra = {}) => ({ playerId: pid, partyId: oid(700 + playerIndex), playerIndex, teamIndex, isHost: playerIndex === 0, ip: IP, isBot: false, ...extra });
const ONE_V_ONE = [team(P1, 0, 0), team(P2, 1, 1)];
const TWO_V_TWO = [team(P1, 0, 0), team(P2, 1, 1), team(P3, 0, 2), team(P4, 1, 3)];

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("set_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
  const snapshot = process.env.REF_SNAPSHOT_URI ? new MongoClient(process.env.REF_SNAPSHOT_URI, { appName: "set_diff" }) : null;
  await snapshot?.connect();

  // A set as the matchmaker made it and the TS websocket left it after a game: game 1's match and config still there,
  // every player online with a fighter.
  const seed = async ({ players = ONE_V_ONE, mode = "1v1", gamesPlayed = 1, scores = [1, 0], extra = {}, characters } = {}) => {
    const ids = [...new Set(players.map((p) => p.playerId))];
    await redis.set(`ranked_set:${SET}`, JSON.stringify({ players, mode, gamesPlayed, scores, checkins: [], ...extra }), { EX: 600 });
    for (const pid of ids) await redis.set(`player_ranked_set:${pid}`, SET, { EX: 600 });
    await redis.set(`match:${SET}`, JSON.stringify({ matchId: SET, resultId: oid(101), tickets: ids.map((id, i) => ({ created_at: 1790000000, matchType: mode, matchmakingRequestId: oid(800 + i), partyId: oid(700 + i), party_size: 1, players: [{ id, region: "MVSI", skill: 0 }] })), status: "pending", createdAt: 1790000000123, matchType: mode, totalPlayers: ids.length, rollbackPort: 50003 }), { EX: 1200 });
    await redis.set(SET, JSON.stringify({ players, matchId: SET, matchKey: "game1key", map: "M001", mode, rollbackPort: 50003, p2p: false }), { EX: 1200 });
    const fighters = characters ?? Object.fromEntries(ids.map((id, i) => [id, ["character_jake", "character_finn", "character_taz", "character_shaggy"][i]]));
    await redis.set(`match_characters:${SET}`, JSON.stringify(fighters), { EX: 1200 });
    for (const [i, pid] of ids.entries()) {
      await redis.hSet(`connections:${pid}`, { id: pid, username: `Player${i + 1}`, hydraUsername: `OpenVersus_${i + 1}`, character: fighters[pid] ?? "character_jake", current_ip: IP });
    }
    await redis.sAdd("online_players", ids);
  };
  const body = (match = SET) => ({ ContainerMatchId: match });

  const steps = [];
  async function step(name, setup, calls, { waitMs = 1000 } = {}) {
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    await setup?.();
    recording = [];
    const started = Date.now();
    const answers = [];
    for (const c of calls) {
      if (c.wait) { await sleep(c.wait); continue; }
      if (c.do) { await c.do(); continue; }
      answers.push(await call(baseUrl, c.route, c.pid, c.body));
    }
    await sleep(waitMs);
    const lines = recording;
    recording = null;
    const registry = (w) => /^(set|zadd|zrem|del) ovs:instance/.test(w);
    // MONITOR escapes quotes and backslashes inside arguments: undone, so the JSON in a write reads (and normalizes) as JSON.
    const all = writes(lines, self).filter((w) => !registry(w)).map((w) => w.replace(/\\(["\\])/g, "$1"));
    const lock = all.filter((w) => w.includes("ranked_set_lock:"));
    const raw = {
      name,
      answers,
      writes: all.filter((w) => !w.startsWith("publish ") && !w.includes("ranked_set_lock:")).sort(),
      published: all.filter((w) => w.startsWith("publish ")).map((w) => {
        const [, channel, ...rest] = w.split(" ");
        const text = rest.join(" ");
        let message;
        try { message = JSON.parse(text); } catch { message = text; }
        return { channel, message };
      }).filter((p) => CHANNELS.has(p.channel)),
      lock: { taken: lock.some((w) => /^set ranked_set_lock:\S+ \S+ /.test(w)), heldAfter: await redis.exists(`ranked_set_lock:${SET}`) },
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^ovs:instance|^ranked_set_lock:/.test(k))),
      sets: Object.fromEntries(await Promise.all([`ranked_set_checkins:${SET}`, "online_players"].map(async (k) => [k, (await redis.sMembers(k)).sort()]))),
      mongo: Object.fromEntries(await Promise.all(["eloratings", "playerstats"].map(async (c) => [c,
        JSON.parse(EJSON.stringify(await db.collection(c).find({}, { promoteValues: false, sort: { account_id: 1 } }).toArray(), { relaxed: false }))]))),
    };
    steps.push(normalize(raw, started));
    process.stdout.write(`${name}: ${answers.map((a) => a.status).join(" ")}\n`);
  }
  const checkin = (pid, match = SET, route = "match_set_checkin") => ({ route, pid, body: body(match) });
  const both = (match = SET) => [checkin(P1, match), checkin(P2, match)];

  // ── Check-ins ────────────────────────────────────────────────────────────────────────────────────────────────────
  await step("no-session", () => seed(), [{ route: "match_set_checkin", pid: null, body: body() }]);
  await step("no-set-for-player", null, [checkin(P1)]);
  await step("set-gone", async () => { await redis.set(`player_ranked_set:${P1}`, SET); }, [checkin(P1)]);
  await step("first-check-in", () => seed(), [checkin(P1)]);
  await step("checked-in-twice", () => seed(), [checkin(P1), checkin(P1)]);
  await step("absent-is-a-check-in", () => seed(), [checkin(P1, SET, "match_set_absent")]);
  await step("next-game", () => seed(), both());
  await step("next-game-2v2", () => seed({ players: TWO_V_TWO, mode: "2v2", scores: [0, 1] }), [...both(), checkin(P3), checkin(P4)]);
  await step("next-game-check-in-and-absent", () => seed(), [checkin(P1), checkin(P1, SET, "match_set_absent"), checkin(P2, SET, "match_set_absent")]);
  await step("stale-absent-after-next-game", () => seed(), [...both(), { wait: 300 }, checkin(P1, SET, "match_set_absent")]);
  // The C# pointer to the current game gone (or never written): the check-in for game 2 counts on both servers.
  await step("check-in-for-game-2-no-pointer", async () => { await seed({ gamesPlayed: 2, scores: [1, 1] }); await redis.set(`match_to_set:${oid(200)}`, SET, { EX: 600 }); }, [checkin(P1, oid(200))]);
  await step("lock-held-by-another-request", async () => { await seed(); await redis.sAdd(`ranked_set_checkins:${SET}`, P1); },
    [{ do: () => redis.set(`ranked_set_lock:${SET}`, "1", { EX: 10 }) }, checkin(P2)], { waitMs: 1500 });

  // ── Disconnects and crashes ──────────────────────────────────────────────────────────────────────────────────────
  await step("disconnected-offline-concedes", async () => { await seed(); await redis.set(`ranked_disconnect:${P2}`, "1", { EX: 600 }); await redis.sRem("online_players", P2); }, [checkin(P1)]);
  await step("disconnected-online-stale-flag", async () => { await seed(); await redis.set(`ranked_disconnect:${P2}`, "1", { EX: 600 }); }, [checkin(P1)]);
  await step("crash-flag-on-set", async () => { await seed(); await redis.set(`match_server_crash:${SET}`, "1", { EX: 600 }); await redis.set(`ranked_disconnect:${P2}`, "1"); }, [checkin(P1)]);
  await step("crash-flag-on-game-2", async () => {
    await seed({ gamesPlayed: 2, scores: [1, 1] });
    await redis.set(`match_to_set:${oid(200)}`, SET, { EX: 600 });
    await redis.set(`ranked_set_match:${SET}`, oid(200), { EX: 600 });
    await redis.set(`match_server_crash:${oid(200)}`, "1", { EX: 600 });
  }, [checkin(P1, oid(200))]);

  // ── The end of a set ─────────────────────────────────────────────────────────────────────────────────────────────
  await step("set-over-2-0", () => seed({ gamesPlayed: 2, scores: [2, 0] }), both());
  await step("set-over-1-2", () => seed({ gamesPlayed: 3, scores: [1, 2] }), both());
  await step("set-over-2v2-2-1", () => seed({ players: TWO_V_TWO, mode: "2v2", gamesPlayed: 3, scores: [2, 1] }), [...both(), checkin(P3), checkin(P4)]);
  await step("set-over-conceded-at-game-end", () => seed({ gamesPlayed: 1, scores: [1, 0], extra: { conceded: true, concedingPlayer: P2 } }), both());
  await step("set-over-no-characters", async () => { await seed({ gamesPlayed: 2, scores: [0, 2] }); await redis.del(`match_characters:${SET}`); for (const p of [P1, P2]) await redis.hDel(`connections:${p}`, "character"); }, both());
  await step("set-over-already-rated", async () => { await seed({ gamesPlayed: 2, scores: [2, 0] }); await redis.set(`elo_processed_set:${SET}`, "set_complete", { EX: 300 }); }, both());
  await step("concede", () => seed({ scores: [0, 1] }), [{ route: "match_set_concede", pid: P2, body: body() }]);
  await step("concede-2v2", () => seed({ players: TWO_V_TWO, mode: "2v2", scores: [1, 0] }), [{ route: "match_set_concede", pid: P3, body: body() }]);
  await step("concede-after-crash", async () => { await seed(); await redis.set(`match_server_crash:${SET}`, "1"); }, [{ route: "match_set_concede", pid: P1, body: body() }]);
  await step("concede-no-set", null, [{ route: "match_set_concede", pid: P1, body: body() }]);
  await step("concede-with-a-bot", () => seed({ players: [team(P1, 0, 0), team(P2, 1, 1, { isBot: true })] }), [{ route: "match_set_concede", pid: P1, body: body() }]);
  await step("faceoff-timeout", async () => { await seed({ gamesPlayed: 0, scores: [0, 0] }); await redis.set(`ranked_disconnect:${P2}`, "1"); }, [{ route: "faceoff_timeout", pid: P1 }]);
  await step("faceoff-timeout-no-set", null, [{ route: "faceoff_timeout", pid: P1 }]);

  // ── Real ratings (REF_SNAPSHOT_URI) ──────────────────────────────────────────────────────────────────────────────
  if (snapshot) {
    const source = snapshot.db();
    for (const mode of ["1v1", "2v2"]) {
      const chars = `characters_${mode}`;
      const size = mode === "1v1" ? 2 : 4;
      const docs = await source.collection("eloratings").find({ [chars]: { $exists: true, $ne: {} } }, { promoteValues: false, sort: { account_id: 1 } }).limit(mode === "1v1" ? 24 : 16).toArray();
      for (let s = 0; s + size <= docs.length; s += size) {
        const n = s / size;
        const group = docs.slice(s, s + size);
        const ids = PLAYERS.slice(0, size);
        const players = mode === "1v1" ? ONE_V_ONE : TWO_V_TWO;
        // Each player's most played character, else (every fourth) one they never played.
        const fighters = Object.fromEntries(group.map((d, i) => {
          const own = Object.entries(EJSON.deserialize(EJSON.serialize(d[chars]))).sort((a, b) => (b[1].wins + b[1].losses) - (a[1].wins + a[1].losses) || a[0].localeCompare(b[0]));
          return [ids[i], (n + i) % 4 === 3 || !own.length ? "character_never_played" : own[0][0]];
        }));
        const copy = async () => {
          for (const [i, d] of group.entries()) {
            const stats = await source.collection("playerstats").findOne({ account_id: d.account_id }, { promoteValues: false });
            await db.collection("eloratings").insertOne({ ...d, _id: new ObjectId(oid(300 + i)), account_id: ids[i], username: `Player${i + 1}` });
            if (stats && n % 2 === 0) {
              const { recent_matches_1v1, recent_matches_2v2, ...rest } = stats;
              await db.collection("playerstats").insertOne({ ...rest, _id: new ObjectId(oid(400 + i)), account_id: ids[i], recent_matches_1v1: [], recent_matches_2v2: [] });
            }
          }
        };
        const scores = [[2, 0], [2, 1], [1, 2], [0, 2]][n % 4];
        if (n % 3 === 2) {
          await step(`replay-${mode}-${n}-concede`, async () => { await seed({ players, mode, scores: [1, 0], characters: fighters }); await copy(); },
            [{ route: "match_set_concede", pid: ids[1], body: body() }]);
        } else {
          await step(`replay-${mode}-${n}-${scores.join("-")}`, async () => { await seed({ players, mode, gamesPlayed: scores[0] + scores[1], scores, characters: fighters }); await copy(); },
            ids.map((id) => checkin(id)));
        }
      }
    }
  }

  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await snapshot?.close();
  await close();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// What is new on every request: times near the request, fresh ids (the harness's own start 0000) renamed in order of
// first appearance, a match's key and map (random), and the lock's token.
function normalize(step, started) {
  const ids = new Map();
  const nearSeconds = (n) => Math.abs(n * 1000 - started) < 120000;
  const nearMs = (n) => Math.abs(n - started) < 120000;
  const rename = (s) => s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/"matchKey":"[A-Za-z0-9+/]{43}="/g, '"matchKey":"<key>"')
    .replace(/"map":"(?!M001")[^"]+"/g, '"map":"<map>"')
    .replace(/\b1\d{12}(\.\d+)?\b/g, (n) => (nearMs(Number(n)) ? "<now ms>" : n))
    .replace(/(?<![\d.])1\d{9}(?![\d.])/g, (n) => (nearSeconds(Number(n)) ? "<now s>" : n));
  const walk = (v, key) => {
    if (Array.isArray(v)) return v.map((x) => walk(x));
    if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x, k)]));
    if (typeof v === "number" && nearMs(v)) return "<now ms>";
    if (typeof v === "string" && key === "matchKey" && /^[A-Za-z0-9+/]{43}=$/.test(v)) return "<key>";
    if (typeof v === "string" && key === "map" && v !== "M001") return "<map>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// ── The deliberate differences (RankedSets, "Unlike there"): each step's must hold exactly, and the rest must match ──

const clone = (v) => JSON.parse(JSON.stringify(v));
const NEW_GAME = "<new id 1>";
// The next game's rollback port: TS took INCR rollback:current_port whatever the deploy mode (1 on an empty Redis:
// the set-match port bug); C# takes the matchmaker's (fixed servers: a random one of 57000..57018). C# also keeps the
// set's current game (ranked_set_match:{set}), and gives the set's keys written with the next game (ranked_set,
// player_ranked_set, match_to_set) 20 min where TS gave 10.
function nextGamePort(ts, cs) {
  const portOf = (run) => [...JSON.stringify(run).matchAll(/\\?"rollbackPort\\?":(\d+)/g)].map((m) => Number(m[1])).filter((p) => p !== 50003);
  const tsPorts = portOf(ts), csPorts = portOf(cs);
  const tsIncr = Number(ts.state["rollback:current_port"]?.value);
  const ok = tsPorts.length > 0 && tsPorts.every((p) => p === tsIncr) && csPorts.length === tsPorts.length && csPorts.every((p) => p >= 57000 && p < 57019)
    && !("rollback:current_port" in cs.state) && cs.state[`ranked_set_match:${SET}`]?.value === NEW_GAME && !(`ranked_set_match:${SET}` in ts.state);
  const strip = (run) => {
    const out = JSON.parse(JSON.stringify(run).replace(/(\\?"rollbackPort\\?":)(\d+)/g, (m, k, p) => (p === "50003" ? m : `${k}0`)));
    delete out.state["rollback:current_port"];
    delete out.state[`ranked_set_match:${SET}`];
    out.writes = out.writes.filter((w) => !w.startsWith("incr rollback:current_port") && !w.startsWith(`set ranked_set_match:${SET}`));
    return out;
  };
  // The set's keys written with the next game: EX 600 on TS, EX 1200 on C#, in the writes and (to the minute) the state.
  const setKey = (key) => key === `ranked_set:${SET}` || key.startsWith("player_ranked_set:") || key.startsWith("match_to_set:");
  let ttlOk = true;
  const ttl = (run, ex, minutes, needed) => {
    let seen = 0;
    run.writes = run.writes.map((w) => {
      const key = w.split(" ")[1];
      // The set itself only as the next game writes it (checkins emptied): a check-in's rewrite is EX 600 on both.
      const nextGames = key !== `ranked_set:${SET}` || w.includes('"checkins":[]');
      if (!(w.startsWith("set ") && setKey(key) && nextGames && w.endsWith(` EX ${ex}`))) return w;
      seen++;
      return w.slice(0, -String(ex).length) + "<set ttl>";
    });
    for (const [key, v] of Object.entries(run.state)) if (setKey(key) && v.ttl === `~${minutes}m`) { v.ttl = "<set ttl>"; seen++; }
    if (needed && seen === 0) ttlOk = false;
    return run;
  };
  const tsOut = ttl(strip(ts), 600, 10, true), csOut = ttl(strip(cs), 1200, 20, true);
  return { ok: ok && ttlOk, ts: tsOut, cs: csOut };
}
const leaverIds = (run) => run.published.filter((p) => p.channel === "ranked_set:leaver").map((p) => p.message.playerIds);
const EXPECTED = {
  "set-gone": {
    why: "the player's set is gone: TS still added the check-in to ranked_set_checkins (nobody reads it again); C# writes nothing",
    check: (ts, cs) => {
      const ok = JSON.stringify(ts.sets[`ranked_set_checkins:${SET}`]) === JSON.stringify([P1]) && cs.sets[`ranked_set_checkins:${SET}`].length === 0;
      const strip = (run) => { const o = clone(run); delete o.state[`ranked_set_checkins:${SET}`]; o.sets[`ranked_set_checkins:${SET}`] = []; o.writes = o.writes.filter((w) => !w.includes(`ranked_set_checkins:${SET}`)); o.lock = null; return o; };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
  "next-game": { why: "the next game's port, the set's current game, and the set keys living 20 min (TS: 10)", check: nextGamePort },
  "next-game-2v2": { why: "the next game's port, the set's current game, and the set keys living 20 min (TS: 10)", check: nextGamePort },
  "next-game-check-in-and-absent": { why: "the next game's port, the set's current game, and the set keys living 20 min (TS: 10)", check: nextGamePort },
  "stale-absent-after-next-game": {
    why: "a late absent for game 1 after game 2 was made: TS counted it toward game 2 (1/2, announced); C# ignores it. And the next game's port",
    check: (ts, cs) => {
      const port = nextGamePort(ts, cs);
      const announced = (run) => run.published.filter((p) => p.channel === "ranked_set:checkin").length;
      const ok = port.ok && JSON.stringify(ts.sets[`ranked_set_checkins:${SET}`]) === JSON.stringify([P1]) && cs.sets[`ranked_set_checkins:${SET}`].length === 0
        && announced(ts) === announced(cs) + 1;
      const strip = (run, late) => {
        const o = clone(run);
        delete o.state[`ranked_set_checkins:${SET}`];
        o.sets[`ranked_set_checkins:${SET}`] = [];
        if (late) {
          // The late check-in's own work, once each: its announcement, SADD, EXPIRE, and the set rewritten with checkins
          // [P1] (the same text as game 1's first check-in wrote: gamesPlayed changes only at a game's end).
          o.published.splice(o.published.findLastIndex((p) => p.channel === "ranked_set:checkin"), 1);
          removeOne(o.writes, (w) => w === `sadd ranked_set_checkins:${SET} ${P1}`);
          removeOne(o.writes, (w) => w === `expire ranked_set_checkins:${SET} 600`);
          removeOne(o.writes, (w) => w.startsWith(`set ranked_set:${SET} `) && w.includes(`"checkins":["${P1}"]`));
          // ... which also left the set waiting for game 2 with P1 already in.
          const set = o.state[`ranked_set:${SET}`];
          if (!set.value.includes(`"checkins":["${P1}"]`)) throw new Error("the late check-in is not in the set");
          set.value = set.value.replace(`"checkins":["${P1}"]`, `"checkins":[]`);
        }
        return o;
      };
      return { ok, ts: strip(port.ts, true), cs: strip(port.cs, false) };
    },
  },
  "lock-held-by-another-request": {
    why: "the second check-in finds the set's lock taken: TS dropped it (2/2 in, no next game, ever); C# waits for the lock (here: its 10 s expiry) and makes the next game",
    check: (ts, cs) => ({
      ok: !(`match_to_set:${NEW_GAME}` in ts.state) && JSON.stringify(ts.sets[`ranked_set_checkins:${SET}`]) === JSON.stringify([P1, P2].sort())
        && cs.state[`match_to_set:${NEW_GAME}`]?.value === SET && cs.published.some((p) => p.channel === "match:notifications"),
      ts: null, cs: null,
    }),
  },
  "crash-flag-on-set": {
    why: "a crash: TS sent only the player checking in back to the menus (the other's check-in then found nothing); C# sends every player of the set",
    check: (ts, cs) => {
      const ok = JSON.stringify(leaverIds(ts)) === JSON.stringify([[P1]]) && JSON.stringify(leaverIds(cs)) === JSON.stringify([[P1, P2]]);
      const strip = (run) => { const o = clone(run); for (const p of o.published) if (p.channel === "ranked_set:leaver") p.message.playerIds = "<players>"; return o; };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
  "crash-flag-on-game-2": {
    why: "a crash in game 2 (flagged under its own id): TS read only the set's flag and counted the check-in; C# drops the set unrated and sends both back",
    check: (ts, cs) => ({
      ok: `ranked_set:${SET}` in ts.state && JSON.stringify(ts.sets[`ranked_set_checkins:${SET}`]) === JSON.stringify([P1])
        && JSON.stringify(ts.published.map((p) => p.channel)) === JSON.stringify(["ranked_set:checkin"])
        && !(`ranked_set:${SET}` in cs.state) && !(`player_ranked_set:${P2}` in cs.state) && !(`ranked_set_match:${SET}` in cs.state)
        && JSON.stringify(leaverIds(cs)) === JSON.stringify([[P1, P2]]) && cs.published.length === 1
        && ts.mongo.eloratings.length === 0 && cs.mongo.eloratings.length === 0,
      ts: null, cs: null,
    }),
  },
  "concede-with-a-bot": {
    why: "a set with a bot: TS rated it (the bot got a rating); C# rates nobody (RatedMatches) and ends the set the same way",
    check: (ts, cs) => {
      const ok = ts.mongo.eloratings.length === 2 && ts.mongo.playerstats.length === 2 && cs.mongo.eloratings.length === 0 && cs.mongo.playerstats.length === 0;
      const strip = (run) => { const o = clone(run); o.mongo = null; return o; };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
};
function removeOne(list, predicate) {
  const at = list.findIndex(predicate);
  if (at < 0) throw new Error("expected write not found");
  list.splice(at, 1);
}

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  const parts = (x, y) => ["answers", "writes", "published", "lock", "state", "sets", "mongo"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const name = a.steps[i]?.name ?? b.steps[i]?.name;
    // ranked_set_match:{set} is C#'s alone (the set's current game): TS never writes it, and C# deletes it with the set.
    // Its SET is asserted where a next game is made (nextGamePort); a C# pointer left behind would still show in state.
    if (a.steps[i]?.writes.some((w) => w.includes("ranked_set_match:"))) {
      differing++;
      console.log(`${name}: TS wrote ranked_set_match`);
    }
    const unpoint = (step) => step && { ...step, writes: step.writes.filter((w) => !w.startsWith("del ranked_set_match:")) };
    const x = a.steps[i], y = unpoint(b.steps[i]);
    const expected = EXPECTED[name];
    if (expected) {
      const { ok, ts, cs } = expected.check(x, y);
      const rest = ts && cs ? parts(ts, cs) : [];
      if (ok && !rest.length) {
        console.log(`${name}: the expected difference (${expected.why})`);
        continue;
      }
      differing++;
      console.log(`${name}: NOT the expected difference (${expected.why})${ok ? `; besides it, differs in ${rest.join(", ")}` : ""}`);
      for (const p of ok ? rest : parts(x, y)) print(p, ok ? ts : x, ok ? cs : y);
      continue;
    }
    const differ = parts(x, y);
    if (!differ.length) continue;
    differing++;
    console.log(`${name}: differs in ${differ.join(", ")}`);
    for (const p of differ) print(p, x, y);
  }
  console.log(differing ? `${differing} step(s) differ` : `no differences (${a.steps.length} steps)`);
  process.exit(differing ? 1 : 0);
}

function print(p, x, y) {
  if (p === "writes") {
    const only = (from, other) => from.filter((w, i) => from.slice(0, i).filter((v) => v === w).length >= other.filter((v) => v === w).length);
    console.log(`  writes only A: ${JSON.stringify(only(x?.writes ?? [], y?.writes ?? [])).slice(0, 3000)}`);
    console.log(`  writes only B: ${JSON.stringify(only(y?.writes ?? [], x?.writes ?? [])).slice(0, 3000)}`);
    return;
  }
  console.log(`  ${p} A: ${String(JSON.stringify(x?.[p])).slice(0, 3000)}`);
  console.log(`  ${p} B: ${String(JSON.stringify(y?.[p])).slice(0, 3000)}`);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: set_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
