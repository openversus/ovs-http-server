// A rollback server's and the P2P nodes' calls about a match (POST /ovs_register, /mvsi_register, /ovs_end_match,
// /mvsi_end_match, /ovs_match_started, /ovs_p2p_ready, /ovs_p2p_failed, /api/ovs_match_status) on the TS server and
// the C# port (match flow: RollbackCallbacks, MatchStatusEvents), scenario by scenario: the answers (status, content
// type, the exact body, and whether the X-OVS-Signature header verifies against the node public key), every Redis write
// and publish the server made (MONITOR; writes compared as a set, publishes in order), the Redis state after, and the
// ratings and set stats in Mongo with their stored types. Run from the repository root (it uses the TS server's
// node_modules):
//
//   node dotnet/tools/matches/callbacks_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/callbacks_diff.mjs diff <ts.json> <cs.json>
//
// The TS server must be PR #49's code as committed, never a working tree with the bench patch.
//
// Scratch stores, wiped before every step (never point these at data you want to keep): REF_REDIS_URL, REF_MONGO_URI
// (as the other harnesses). Both servers need the same MatchUpdateKey (REF_MATCH_UPDATE_KEY, their MATCHUPDATEKEY),
// the same node signing key (P2P_NODE_SIGNING_KEY_FILE; REF_NODE_PUBLIC_KEY is its public half, a
// node-config-public-key.txt), UDP_SERVER_IP and UDP_PORT, and fixed rollback servers (ON_DEMAND_ROLLBACK=0: a relay
// request deploys nothing), and the C# match flow MatchEnd:Enabled off (the TS websocket ends the match in both runs: the
// match:end publish is compared). ECDSA signatures differ on every signing, so a signature is compared by whether it verifies.
// The dodge steps also need REF_WS_URL and REF_JWT_SECRET: the TS websocket (PR #49's code as committed) turns the TS
// server's ranked_set:fullrankupdate into each connected player's FullRankUpdate and delivers what C# sends through
// ws:send. In those steps the players online get a fake game for the step (connected after the setup, closed after), and
// what their games were sent is compared; each run is checked to use only its own channel.
import fs from "node:fs";
import crypto from "node:crypto";
import { require, need, openScratch, openMonitor, writes, state } from "../refdiff/refdiff.mjs";
import { connectPlayers } from "../refdiff/gateway.mjs";

const { EJSON } = require(process.cwd() + "/node_modules/bson");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const oid = (n) => "00000000000000000015" + String(n).padStart(4, "0");
const [P1, P2, BOT, SPEC] = [1, 2, 3, 4].map(oid);
const MATCH = oid(100), SET = oid(101), OTHER = oid(102);
const KEY = "matchkey-from-the-game";
const IP = "198.51.100.8";
const CHANNELS = new Set(["game_server_ready:notifications", "match:end"]);
let publicKey;
const verifies = (bytes, signature) => {
  publicKey ??= crypto.createPublicKey({ key: Buffer.from(fs.readFileSync(need("REF_NODE_PUBLIC_KEY"), "utf8").trim(), "base64"), format: "der", type: "spki" });
  return crypto.verify("sha256", bytes, { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(signature, "base64"));
};

// As the rollback server sends it (HTTPHelper.PostJsonAsync): JSON, MatchUpdateKey and BodyAsBase64 headers.
async function post(baseUrl, route, body, { key = need("REF_MATCH_UPDATE_KEY"), timeout = 10000 } = {}) {
  const text = body === undefined ? undefined : JSON.stringify(body);
  const headers = { "content-type": "application/json", "x-real-ip": IP };
  if (key !== null) headers.MatchUpdateKey = key;
  if (text !== undefined) headers.BodyAsBase64 = Buffer.from(text).toString("base64");
  let response, bytes;
  try {
    response = await fetch(baseUrl + route, { method: "POST", headers, body: text, signal: AbortSignal.timeout(timeout) });
    bytes = Buffer.from(await response.arrayBuffer());
  } catch (e) {
    return { route, status: `<no answer: ${e.name}>` };
  }
  const answer = { route, status: response.status, type: response.headers.get("content-type"), body: bytes.toString("utf8") };
  const signature = response.headers.get("x-ovs-signature");
  answer.signature = signature === null ? null : verifies(bytes, signature) ? "<verifies>" : "<DOES NOT VERIFY>";
  return answer;
}

const ROSTER = () => [
  { playerId: P1, playerIndex: 0, teamIndex: 0, isHost: true, ip: IP, isBot: false },
  { playerId: P2, playerIndex: 1, teamIndex: 1, isHost: false, ip: "198.51.100.9", isBot: false },
  { playerId: BOT, playerIndex: 3, teamIndex: 1, isHost: false, ip: "", isBot: true },
  { playerId: SPEC, playerIndex: 8888, teamIndex: -1, isHost: false, ip: "198.51.100.10", isSpectator: true },
];
const ONE_V_ONE = () => ROSTER().slice(0, 2);

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("callbacks_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];

  // A match as a launcher left it: its config ({match}) and match:{match}, and its players' connections.
  const seed = async ({ players = ROSTER(), p2p = false, config = {}, match = {}, noMatch = false } = {}) => {
    await redis.set(MATCH, JSON.stringify({ players, matchId: MATCH, matchKey: KEY, map: "M001", mode: "1v1", rollbackPort: 57003, p2p, ...config }), { EX: 1200 });
    if (!noMatch) {
      await redis.set(`match:${MATCH}`, JSON.stringify({ matchId: MATCH, resultId: oid(103), tickets: [], status: "pending", createdAt: 1790000000123, matchType: "1v1", totalPlayers: 2, rollbackPort: 57003, ...match }), { EX: 1200 });
    }
    await redis.hSet(`connections:${P1}`, { id: P1, username: "Player1", character: "character_jake" });
    await redis.hSet(`connections:${P2}`, { id: P2, username: "Pläyer ✓ 2", character: "" });
    await redis.hSet(`connections:${SPEC}`, { id: SPEC, username: "Watcher" });
  };
  // P2's set, as the matchmaker and the TS websocket leave it after game 1; ranked_set_match is C#'s pointer.
  const seedSet = async () => {
    await redis.set(`ranked_set:${SET}`, JSON.stringify({ players: ONE_V_ONE(), mode: "1v1", gamesPlayed: 1, scores: [1, 0], checkins: [] }), { EX: 600 });
    for (const pid of [P1, P2]) await redis.set(`player_ranked_set:${pid}`, SET, { EX: 600 });
    await redis.sAdd(`ranked_set_checkins:${SET}`, P1);
    await redis.set(`ranked_set_match:${SET}`, MATCH, { EX: 1800 });
    await redis.set(`match_to_set:${MATCH}`, SET, { EX: 1200 });
    await redis.set(`match_characters:${SET}`, JSON.stringify({ [P1]: "character_jake", [P2]: "character_finn" }), { EX: 1200 });
  };
  const online = (...ids) => redis.sAdd("online_players", ids);
  const keyed = (extra = {}) => ({ matchId: MATCH, key: KEY, ...extra });
  const status = (Event, extra = {}) => ({
    Timestamp: { Year: 2026, Month: 10, Day: 5, Hour: 12, Minute: 0, Second: 0, Millisecond: 0 },
    Event, Description: Event, matchId: MATCH, key: KEY, NumPlayers: extra.PlayerIds?.length ?? 0, PlayerId: "", PlayerIds: [], ...extra,
  });
  const disconnect = (pid) => status("PlayerDisconnect", { PlayerId: pid, PlayerIds: [pid] });

  const steps = [];
  async function step(name, setup, calls, { waitMs = 700, games: withGames = false } = {}) {
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    await setup?.();
    // A fake game for each player online, so that the TS websocket holds a socket exactly for them.
    const games = withGames
      ? await connectPlayers(need("REF_WS_URL"), (await redis.sMembers("online_players")).sort().map((id) => ({ id, token: jwt.sign({ id, profile_id: id, wb_network_id: id }, need("REF_JWT_SECRET")) })))
      : null;
    if (games) await sleep(200);
    recording = [];
    const started = Date.now();
    const answers = [];
    for (const c of calls) {
      if (c.wait) { await sleep(c.wait); continue; }
      if (c.do) { await c.do(); continue; }
      answers.push(await post(baseUrl, c.route, c.body, c.options));
    }
    await sleep(waitMs);
    const lines = recording;
    recording = null;
    const frames = games && Object.fromEntries((await redis.sMembers("online_players")).sort().map((id) => [id, games.frames(id).filter((f) => !f?.raw)]));
    // The TS websocket's presence for the fake games (a pong can land in a step) is left out.
    const all = writes(lines, self).filter((w) => !/^(set|zadd|zrem|del) ovs:instance|^zadd player_heartbeats|^(sadd|srem) online_players|^(zadd|expire) active_ip_accounts:/.test(w))
      .map((w) => w.replace(/\\(["\\])/g, "$1"));
    const raw = {
      name,
      answers,
      frames,
      channels: [...new Set(all.filter((w) => w.startsWith("publish ")).map((w) => w.split(" ")[1]))],
      writes: all.filter((w) => !w.startsWith("publish ")).sort(),
      published: all.filter((w) => w.startsWith("publish ")).map((w) => {
        const [, channel, ...rest] = w.split(" ");
        const text = rest.join(" ");
        let message;
        try { message = JSON.parse(text); } catch { message = text; }
        return { channel, message };
      }).filter((p) => CHANNELS.has(p.channel)),
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^ovs:instance|^player_heartbeats$|^active_ip_accounts:/.test(k))),
      lists: Object.fromEntries(await Promise.all([P1, P2, BOT, SPEC].map(async (p) => [p, await redis.lRange(`dll_notifications:${p}`, 0, -1)]))),
      mongo: Object.fromEntries(await Promise.all(["eloratings", "playerstats"].map(async (c) => [c,
        JSON.parse(EJSON.stringify(await db.collection(c).find({}, { promoteValues: false, sort: { account_id: 1 } }).toArray(), { relaxed: false }))]))),
    };
    if (games) {
      // Closed after the step's record; the TS websocket's disconnect handling runs before the next step wipes the stores.
      games.close();
      await sleep(600);
    }
    steps.push(normalize(raw, started));
    process.stdout.write(`${name}: ${answers.map((a) => a.status).join(" ")}\n`);
  }
  const call = (route, body, options) => ({ route, body, options });

  // ── /ovs_register ────────────────────────────────────────────────────────────────────────────────────────────────
  await step("register-unknown-match", () => seed(), [call("/ovs_register", { matchId: OTHER, key: KEY })]);
  await step("register-key-mismatch", () => seed(), [call("/ovs_register", keyed({ key: "another" }))]);
  await step("register-no-key", () => seed(), [call("/ovs_register", { matchId: MATCH })]);
  await step("register-no-body", () => seed(), [call("/ovs_register", undefined)]);
  await step("register-hostname-ignored", () => seed(), [call("/ovs_register", keyed({ hostname: "rollback-7" }))]);
  await step("register-relay", () => seed(), [call("/ovs_register", keyed())]);
  await step("register-twice", () => seed(), [call("/ovs_register", keyed()), call("/ovs_register", keyed())]);
  await step("register-field-edges", () => seed({
    players: [
      { playerId: P1, playerIndex: 0, teamIndex: 0, isHost: true, ip: IP, isBot: false },
      { playerId: P2, playerIndex: null, teamIndex: 1, isSpectator: null, isBot: "" },
      { playerId: BOT, playerIndex: 2, teamIndex: 1, isBot: "yes" },
      { playerIndex: 5, teamIndex: 0, isBot: 0, isHost: null, ip: null },
      { playerId: SPEC, playerIndex: 8888, teamIndex: -1, isSpectator: 1 },
    ],
  }), [call("/ovs_register", keyed())]);
  await step("register-no-match-port", () => seed({ noMatch: true }), [call("/ovs_register", keyed())]);
  await step("register-match-port-zero", () => seed({ match: { rollbackPort: 0 } }), [call("/ovs_register", keyed())]);
  await step("register-p2p-held", () => seed({ p2p: true }), [call("/ovs_register", keyed())]);
  await step("register-p2p-after-relay-asked", async () => { await seed({ p2p: true }); await redis.set(`p2p_relay:${MATCH}`, "1", { EX: 1200 }); }, [call("/ovs_register", keyed())]);

  // ── P2P ──────────────────────────────────────────────────────────────────────────────────────────────────────────
  await step("p2p-ready", () => seed({ p2p: true }), [call("/ovs_p2p_ready", keyed())]);
  await step("p2p-ready-not-p2p", () => seed(), [call("/ovs_p2p_ready", keyed())]);
  await step("p2p-ready-key-mismatch", () => seed({ p2p: true }), [call("/ovs_p2p_ready", keyed({ key: "another" }))]);
  await step("p2p-failed-twice", () => seed({ p2p: true }), [call("/ovs_p2p_failed", keyed()), call("/ovs_p2p_failed", keyed())]);
  await step("p2p-failed-not-p2p", () => seed(), [call("/ovs_p2p_failed", keyed())]);
  await step("p2p-failed-key-mismatch", () => seed({ p2p: true }), [call("/ovs_p2p_failed", keyed({ key: "another" }))]);
  await step("p2p-failed-then-relay-registers", () => seed({ p2p: true }), [call("/ovs_register", keyed()), call("/ovs_p2p_failed", keyed()), call("/ovs_register", keyed())]);

  // ── End and start ────────────────────────────────────────────────────────────────────────────────────────────────
  await step("end-match", () => seed(), [call("/ovs_end_match", keyed())]);
  await step("end-match-key-mismatch", () => seed(), [call("/ovs_end_match", keyed({ key: "another" }))]);
  await step("end-match-from-a-node", () => seed({ p2p: true }), [call("/ovs_end_match", keyed(), { key: "MisconfiguredMatchUpdateKey" })]);
  await step("match-started-from-a-node", () => seed({ p2p: true }), [call("/ovs_match_started", keyed(), { key: "MisconfiguredMatchUpdateKey" })]);
  await step("match-started-key-mismatch", () => seed({ p2p: true }), [call("/ovs_match_started", keyed({ key: "another" }))]);

  // ── The MVSI forms ───────────────────────────────────────────────────────────────────────────────────────────────
  await step("mvsi-register", () => seed(), [call("/mvsi_register", keyed())]);
  await step("mvsi-register-key-mismatch", () => seed(), [call("/mvsi_register", keyed({ key: "another" }))]);
  await step("mvsi-end-match", () => seed(), [call("/mvsi_end_match", keyed())]);
  await step("mvsi-register-no-matchid", () => seed(), [call("/mvsi_register", { key: KEY }, { timeout: 3000 })]);

  // ── Match status: the key and the events ─────────────────────────────────────────────────────────────────────────
  await step("status-no-key", () => seed(), [call("/api/ovs_match_status", status("MatchStarted"), { key: null })]);
  await step("status-wrong-key", () => seed(), [call("/api/ovs_match_status", status("MatchStarted"), { key: "Ref-Match-Update-Kez" })]);
  await step("status-key-other-case", () => seed(), [call("/api/ovs_match_status", status("MatchStarted"), { key: need("REF_MATCH_UPDATE_KEY").toUpperCase() })]);
  await step("status-old-path", () => seed(), [call("/ovs_match_status", status("MatchStarted"))]);
  await step("status-no-body", () => seed(), [call("/api/ovs_match_status", undefined)]);
  await step("status-heartbeat", () => seed(), [call("/api/ovs_match_status", status("HeartBeat"))]);
  await step("status-match-started", () => seed(), [call("/api/ovs_match_status", status("MatchStarted"))]);
  await step("status-terminating-before-start", () => seed(), [call("/api/ovs_match_status", status("TerminatingError"))]);
  await step("status-terminating-after-start", () => seed(), [call("/api/ovs_match_status", status("MatchStarted")), call("/api/ovs_match_status", status("TerminatingError"))]);
  await step("status-all-left-after-start", () => seed(), [call("/api/ovs_match_status", status("MatchStarted")), call("/api/ovs_match_status", status("AllPlayersDisconnected"))]);
  await step("status-all-left-after-result", async () => { await seed(); await redis.set(`game_result_received:${MATCH}`, "1", { EX: 600 }); },
    [call("/api/ovs_match_status", status("MatchStarted")), call("/api/ovs_match_status", status("AllPlayersDisconnected"))]);
  await step("status-match-ended", () => seed(), [call("/api/ovs_match_status", status("MatchStarted")), call("/api/ovs_match_status", status("MatchEnded"))]);

  // ── Match status: PlayerDisconnect ───────────────────────────────────────────────────────────────────────────────
  await step("disconnect-unknown-player", () => seed(), [call("/api/ovs_match_status", disconnect("Unknown"))]);
  await step("disconnect-after-end", async () => { await seed(); await redis.set(`match_ended:${MATCH}`, "1"); }, [call("/api/ovs_match_status", disconnect(P2))]);
  await step("disconnect-after-result", async () => { await seed(); await redis.set(`game_result_received:${MATCH}`, "1"); }, [call("/api/ovs_match_status", disconnect(P2))]);
  await step("disconnect-after-crash", async () => { await seed(); await redis.set(`match_server_crash:${MATCH}`, "1"); }, [call("/api/ovs_match_status", disconnect(P2))]);
  await step("disconnect-still-online", async () => { await seed(); await seedSet(); await online(P1, P2); await redis.set(`match_started:${MATCH}`, "1"); },
    [call("/api/ovs_match_status", disconnect(P2)), call("/api/ovs_match_status", disconnect(P1))]);
  await step("disconnect-still-online-no-config", async () => { await online(P1, P2); },
    [call("/api/ovs_match_status", status("PlayerDisconnect", { PlayerId: P2, PlayerIds: [P1, P2] }))]);
  await step("disconnect-spectator-online", async () => { await seed(); await seedSet(); await online(P1, P2, SPEC); await redis.set(`match_started:${MATCH}`, "1"); },
    [call("/api/ovs_match_status", disconnect(SPEC))]);
  await step("disconnect-spectator-mid-game", async () => { await seed(); await online(P1, P2); await redis.set(`match_started:${MATCH}`, "1"); },
    [call("/api/ovs_match_status", disconnect(SPEC))]);
  await step("disconnect-mid-game", async () => { await seed(); await online(P1); await redis.set(`match_started:${MATCH}`, "1"); }, [call("/api/ovs_match_status", disconnect(P2))]);
  await step("dodge-rated", async () => { await seed({ players: ONE_V_ONE() }); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-rated-team-0-leaves", async () => { await seed({ players: ONE_V_ONE() }); await online(P2); }, [call("/api/ovs_match_status", disconnect(P1))], { games: true });
  await step("dodge-rated-in-set", async () => { await seed({ players: ONE_V_ONE() }); await seedSet(); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-twice", async () => { await seed({ players: ONE_V_ONE() }); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2)), call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-already-processed", async () => { await seed({ players: ONE_V_ONE() }); await online(P1); await redis.set(`elo_processed:${MATCH}`, "1"); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-custom-game", async () => { await seed({ players: ONE_V_ONE(), config: { isCustomGame: true } }); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-with-a-bot", async () => { await seed({ players: ROSTER().slice(0, 3), config: { mode: "2v2" } }); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-rift", async () => { await seed({ players: ONE_V_ONE(), match: { isPasswordMatch: true } }); await online(P1); }, [call("/api/ovs_match_status", disconnect(P2))], { games: true });
  await step("dodge-spectator", async () => { await seed(); await online(P1, P2); }, [call("/api/ovs_match_status", disconnect(SPEC))], { games: true });

  monitor.destroy();
  await close();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, ranAt: new Date().toISOString(), steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// What is new on every request: times near the request and fresh ids (the harness's own start 0000) renamed in order of
// first appearance.
function normalize(step, started) {
  const ids = new Map();
  const nearSeconds = (n) => Math.abs(n * 1000 - started) < 120000;
  const nearMs = (n) => Math.abs(n - started) < 120000;
  const rename = (s) => s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/\b1\d{12}(\.\d+)?\b/g, (n) => (nearMs(Number(n)) ? "<now ms>" : n))
    .replace(/(?<![\d.])1\d{9}(?![\d.])/g, (n) => (nearSeconds(Number(n)) ? "<now s>" : n));
  const walk = (v) => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x)]));
    if (typeof v === "number" && nearMs(v)) return "<now ms>";
    if (typeof v === "number" && nearSeconds(v)) return "<now s>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// ── The deliberate differences (RollbackCallbacks, MatchStatusEvents: "Unlike there"): each must hold exactly, and the
// rest of the step must match ──

const clone = (v) => JSON.parse(JSON.stringify(v));
const empty = (run) => run.writes.length === 0 && run.published.length === 0 && run.mongo.eloratings.length === 0;
const unanswered = (a) => typeof a.status === "string" && a.status.startsWith("<no answer");
const emptyAnswer = (a) => a.status === 200 && a.body === "" && a.type?.startsWith("text/html");

// The pointer ranked_set_match:{set} is C#'s (RankedSets: the set's current game); TS never writes it. C# deletes it with
// every set it drops (under the set's id, which is the match's when the player's set pointer is gone): those deletes are
// taken out of every C# step (diffRuns), and where the pointer existed, its absence after is asserted here.
function pointer(ts, cs) {
  const key = `ranked_set_match:${SET}`;
  const ok = ts.state[key] !== undefined && cs.state[key] === undefined;
  const strip = (run) => { const o = clone(run); delete o.state[key]; return o; };
  return { ok, ts: strip(ts), cs: strip(cs) };
}

// A dodge TS rated and C# does not (RatedMatches): TS wrote ratings and set stats and sent the player online their ranks;
// C# did none of it. Everything else must be the same.
function unrated(ts, cs) {
  const ranks = (run) => Object.values(run.frames).flat().filter((f) => f?.data?.template_id === "FullRankUpdate").length;
  const ok = ts.mongo.eloratings.length > 0 && cs.mongo.eloratings.length === 0 && cs.mongo.playerstats.length === 0 && ranks(ts) === 1 && ranks(cs) === 0;
  const strip = (run) => {
    const o = clone(run);
    o.mongo = null;
    for (const id of Object.keys(o.frames)) o.frames[id] = o.frames[id].filter((f) => f?.data?.template_id !== "FullRankUpdate");
    return o;
  };
  return { ok, ts: strip(ts), cs: strip(cs) };
}

const EXPECTED = {
  "disconnect-still-online": { why: "C# also deletes the set's current game (ranked_set_match, its own pointer)", check: pointer },
  "dodge-rated-in-set": { why: "C# also deletes the set's current game (ranked_set_match, its own pointer)", check: pointer },
  "disconnect-spectator-online": {
    why: "a spectator's disconnect (websocket up): TS crashed the match (flag, set dropped, everyone idle and sent match_cancel); C# does nothing (decided 2026-10-05)",
    check: (ts, cs) => {
      const ok = ts.writes.includes(`set match_server_crash:${MATCH} 1 EX 600`) && ts.lists[P1].length === 1 && ts.lists[P2].length === 1 && empty(cs)
        && Object.values(cs.lists).every((l) => l.length === 0) && cs.state[`ranked_set:${SET}`] !== undefined;
      const strip = (run) => ({ ...clone(run), writes: null, published: null, state: null, lists: null });
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
  "disconnect-spectator-mid-game": {
    why: "a spectator's disconnect (websocket down, mid-game): TS flagged ranked_disconnect for the spectator; C# does nothing (decided 2026-10-05)",
    check: (ts, cs) => {
      const ok = JSON.stringify(ts.writes) === JSON.stringify([`set ranked_disconnect:${SPEC} 1 EX 600`]) && empty(cs);
      const strip = (run) => { const o = clone(run); o.writes = null; delete o.state[`ranked_disconnect:${SPEC}`]; return o; };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
  "dodge-with-a-bot": { why: "a dodge in a match with a bot: TS rated it (the bot too); C# does not (RatedMatches), and does the rest the same", check: unrated },
  "dodge-rift": { why: "a dodge in a password match (rift): TS rated it; C# does not (RatedMatches), and does the rest the same", check: unrated },
  "mvsi-register-no-matchid": {
    why: "an MVSI register without matchId: TS threw and never answered; C# answers \"\"",
    check: (ts, cs) => {
      const ok = unanswered(ts.answers[0]) && emptyAnswer(cs.answers[0]);
      const strip = (run) => ({ ...clone(run), answers: null });
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
};

// The dodge flag (decided 2026-10-05: written only in a rated set's game, naming the set): TS wrote ranked_disconnect "1"
// after any started match; C# writes the set's id (the match's when it has no set pointer: game 1) in a rated game, and
// nothing in one RatedMatches does not count (a bot, a rift). Asserted on these steps, then C#'s form taken as TS's.
const FLAG_NAMED = new Set(["dodge-rated", "dodge-rated-team-0-leaves", "dodge-rated-in-set", "dodge-twice"]);
const FLAG_NONE = new Set(["dodge-with-a-bot", "dodge-rift", "disconnect-mid-game"]);
function dodgeFlag(name, ts, cs) {
  if (!FLAG_NAMED.has(name) && !FLAG_NONE.has(name)) return true;
  const flagOf = (run) => run.writes.filter((w) => w.startsWith("set ranked_disconnect:"));
  const [tsFlag, ...more] = flagOf(ts);
  const m = /^set (ranked_disconnect:\S+) 1 EX 600$/.exec(tsFlag ?? "");
  if (!m || more.length) return false;
  const csFlags = flagOf(cs);
  if (FLAG_NAMED.has(name)) {
    const named = new RegExp(`^set ${m[1]} (${MATCH}|${SET}) EX 600$`).exec(csFlags[0] ?? "");
    if (csFlags.length !== 1 || !named || cs.state[m[1]]?.value !== named[1]) return false;
    cs.writes[cs.writes.indexOf(csFlags[0])] = tsFlag;
    cs.state[m[1]] = { ...cs.state[m[1]], value: "1" };
    cs.writes.sort();
    return true;
  }
  if (csFlags.length || m[1] in cs.state) return false;
  ts.writes.splice(ts.writes.indexOf(tsFlag), 1);
  delete ts.state[m[1]];
  return true;
}

// FullRankUpdate's season: TS's is always Season:SeasonFive, C#'s Season:Current (decided: as ranked_data and the login).
function season(run, wanted) {
  let ok = true;
  for (const f of Object.values(run?.frames ?? {}).flat()) {
    if (f?.data?.template_id !== "FullRankUpdate") continue;
    const keys = Object.keys(f.data.SeasonalData ?? {});
    if (keys.length !== 1 || !wanted(keys[0])) ok = false;
    f.data.SeasonalData = { "<season>": f.data.SeasonalData[keys[0]] };
  }
  return ok;
}

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  const parts = (x, y) => ["answers", "frames", "writes", "published", "state", "lists", "mongo"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const name = a.steps[i]?.name ?? b.steps[i]?.name;
    // Each run sends the ranks on its own channel: TS ranked_set:fullrankupdate, C# ws:send.
    if (a.steps[i]?.channels.includes("ws:send") || b.steps[i]?.channels.includes("ranked_set:fullrankupdate")) {
      differing++;
      console.log(`${name}: a message on the other server's channel (A: ${a.steps[i]?.channels}; B: ${b.steps[i]?.channels})`);
    }
    if (a.steps[i]?.writes.some((w) => w.includes("ranked_set_match:"))) {
      differing++;
      console.log(`${name}: A (TS) wrote ranked_set_match`);
    }
    const unpoint = (run) => run && { ...run, writes: run.writes.filter((w) => !w.startsWith("del ranked_set_match:")) };
    const x = a.steps[i], y = unpoint(b.steps[i]);
    if (x && y && !dodgeFlag(name, x, y)) {
      differing++;
      console.log(`${name}: NOT the decided dodge flag (TS "1"; C# the set's id in a rated game, none otherwise): A ${JSON.stringify(x.writes.filter((w) => w.includes("ranked_disconnect")))} B ${JSON.stringify(y.writes.filter((w) => w.includes("ranked_disconnect")))}`);
    }
    if (!season(x, (k) => k === "Season:SeasonFive") || !season(y, (k) => /^Season:\w+$/.test(k))) {
      differing++;
      console.log(`${name}: a FullRankUpdate season is not TS's Season:SeasonFive / C#'s current one`);
    }
    // Every signed answer must verify, on both sides.
    for (const [side, run] of [["A", x], ["B", y]]) {
      if (run?.answers.some((ans) => ans.signature === "<DOES NOT VERIFY>")) {
        differing++;
        console.log(`${name}: ${side} sent a signature that does not verify`);
      }
    }
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
  console.error("usage: callbacks_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
