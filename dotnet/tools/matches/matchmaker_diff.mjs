// The matchmaking worker of the TS server (src/matchmaking-worker.ts) and of the C# port (Core/Matchmaking), scenario by
// scenario: each seeds the queues (the `1v1` and `2v2` ticket lists), the players' heartbeats, addresses and blocks in
// one transaction, lets the ONE worker running against the scratch stores work for a few ticks, and records what it
// wrote (MONITOR: command and key), what it published (parsed) and the state after. Run from the repository root:
//
//   node dotnet/tools/matches/matchmaker_diff.mjs run <out.json> [scenario filter]
//   node dotnet/tools/matches/matchmaker_diff.mjs diff <ts.json> <cs.json>
//
// Environment: REF_REDIS_URL (the scratch Redis, wiped before every scenario), REF_PORT_LOW / REF_PORT_HIGH (the fixed
// rollback ports, [low, high)). Exactly one worker must run against REF_REDIS_URL (the TS one: npm run worker; or the
// C# matchmaker, OpenVersus.Server.Matchmaking), and nothing else that reacts to the published channels (stop the TS
// websocket). The worker's lock (matchmaking:lock:*) is left out: it is taken every tick, matched or not.
//
// What is random is checked, then replaced: every party on one team, teams of half the players each, player indexes
// place * 2 + team, exactly one host, each player's ip from player:{id} (none when it has none), the map one of the
// enabled ones for the mode (or PVE_03, the 1v1 surprise), the match key 32 bytes, the port in [low, high). A failed
// check is recorded (and so differs).
import fs from "node:fs";
import { need, openMonitor } from "../refdiff/refdiff.mjs";
import { require } from "../refdiff/refdiff.mjs";

const { createClient } = require(process.cwd() + "/node_modules/redis");
const oid = (n) => "0000000000000000000f" + String(n).padStart(4, "0");
const P = (n) => oid(n);
const IP = (n) => `198.51.100.${n}`;
const LOW = Number(need("REF_PORT_LOW")), HIGH = Number(need("REF_PORT_HIGH"));
const enabledMaps = (mode) => JSON.parse(fs.readFileSync(`src/data/maps${mode}.json`, "utf8")).map((o) => Object.values(o)[0]).filter((m) => m.enabled).map((m) => m.id);
const MAPS = { "1v1": [...enabledMaps("1v1"), "PVE_03"], "2v2": enabledMaps("2v2") };

// A ticket as the TS queueMatch makes it (key order included); ages in seconds.
let seq = 0;
function ticket(mode, players, { age = 1, skills = [], ip = true } = {}) {
  seq++;
  return {
    created_at: Math.floor(Date.now() / 1000) - age,
    matchType: mode,
    partyLeaderId: P(players[0]),
    matchmakingRequestId: oid(500 + seq),
    partyId: oid(700 + seq),
    party_size: players.length,
    players: players.map((n, i) => ({ id: P(n), region: "MVSI", skill: skills[i] ?? 0, ...(ip ? { ip: IP(n) } : {}) })),
  };
}

// Each scenario: the tickets per queue (list order), and the players' state.
// heartbeats: "all" (now), "none" (no zset at all), or {n: seconds ago} (missing players: no score).
const SCENARIOS = {
  "1v1-pair": () => ({ q1: [ticket("1v1", [1], { age: 2 }), ticket("1v1", [2])] }),
  "1v1-skill-apart-young": () => ({ q1: [ticket("1v1", [1], { age: 2, skills: [0] }), ticket("1v1", [2], { skills: [600] })] }),
  "1v1-skill-apart-waiting": () => ({ q1: [ticket("1v1", [1], { age: 7, skills: [0] }), ticket("1v1", [2], { age: 6, skills: [400] })] }),
  "1v1-stricter-range-wins": () => ({ q1: [ticket("1v1", [1], { age: 30, skills: [0] }), ticket("1v1", [2], { age: 1, skills: [1000] })] }),
  "1v1-both-waited": () => ({ q1: [ticket("1v1", [1], { age: 30, skills: [0] }), ticket("1v1", [2], { age: 20, skills: [1000] })] }),
  "1v1-blocked": () => ({ q1: [ticket("1v1", [1], { age: 3 }), ticket("1v1", [2], { age: 2 }), ticket("1v1", [3])], blocks: { 1: [2] } }),
  "1v1-blocked-by-the-other": () => ({ q1: [ticket("1v1", [1], { age: 3 }), ticket("1v1", [2])], blocks: { 2: [1] } }),
  "1v1-duplicate-player": () => ({ q1: [ticket("1v1", [1], { age: 4 }), ticket("1v1", [2], { age: 3 }), ticket("1v1", [1], { age: 1 })] }),
  "1v1-silent-player": () => ({ q1: [ticket("1v1", [1], { age: 3 }), ticket("1v1", [2], { age: 2 }), ticket("1v1", [3])], heartbeats: { 1: 0, 2: 60, 3: 0 } }),
  "1v1-no-heartbeat-for-one": () => ({ q1: [ticket("1v1", [1], { age: 3 }), ticket("1v1", [2], { age: 2 }), ticket("1v1", [3])], heartbeats: { 1: 0, 3: 0 } }),
  "1v1-no-heartbeats-at-all": () => ({ q1: [ticket("1v1", [1], { age: 2 }), ticket("1v1", [2])], heartbeats: "none" }),
  "1v1-lone-silent-ticket-stays": () => ({ q1: [ticket("1v1", [1])], heartbeats: { 1: 60 } }),
  "1v1-party-of-two-waits": () => ({ q1: [ticket("1v1", [1, 2], { age: 2 }), ticket("1v1", [3])] }),
  "1v1-four-in-two-ticks": () => ({ q1: [ticket("1v1", [1], { age: 4 }), ticket("1v1", [2], { age: 3 }), ticket("1v1", [3], { age: 2 }), ticket("1v1", [4])], wait: 7000 }),
  "1v1-no-ip": () => ({ q1: [ticket("1v1", [1], { age: 2 }), ticket("1v1", [2])], noIp: [1] }),
  "2v2-four-solos": () => ({ q2: [ticket("2v2", [1], { age: 4 }), ticket("2v2", [2], { age: 3 }), ticket("2v2", [3], { age: 2 }), ticket("2v2", [4])] }),
  "2v2-duo-and-solos": () => ({ q2: [ticket("2v2", [1], { age: 4 }), ticket("2v2", [2, 3], { age: 3 }), ticket("2v2", [4])] }),
  "2v2-two-duos": () => ({ q2: [ticket("2v2", [1, 2], { age: 3 }), ticket("2v2", [3, 4])] }),
  "2v2-three-players": () => ({ q2: [ticket("2v2", [1], { age: 2 }), ticket("2v2", [2, 3])] }),
  "2v2-five-solos": () => ({ q2: [1, 2, 3, 4, 5].map((n) => ticket("2v2", [n], { age: 10 - n })) }),
  "2v2-skill-apart-young": () => ({ q2: [ticket("2v2", [1, 2], { age: 2, skills: [1000, 1000] }), ticket("2v2", [3], { skills: [0] }), ticket("2v2", [4], { skills: [0] })] }),
  "2v2-skill-apart-oldest-waited": () => ({ q2: [ticket("2v2", [1, 2], { age: 30, skills: [1000, 1000] }), ticket("2v2", [3], { age: 1, skills: [0] }), ticket("2v2", [4], { skills: [0] })] }),
  "2v2-blocked": () => ({ q2: [ticket("2v2", [1], { age: 5 }), ticket("2v2", [2], { age: 4 }), ticket("2v2", [3], { age: 3 }), ticket("2v2", [4], { age: 2 }), ticket("2v2", [5])], blocks: { 1: [4] } }),
  "2v2-duplicate-player": () => ({ q2: [ticket("2v2", [1], { age: 5 }), ticket("2v2", [1, 2], { age: 4 }), ticket("2v2", [3], { age: 3 }), ticket("2v2", [4])] }),
  "both-queues": () => ({ q1: [ticket("1v1", [1], { age: 2 }), ticket("1v1", [2])], q2: [ticket("2v2", [3, 4], { age: 2 }), ticket("2v2", [5]), ticket("2v2", [6])] }),
};

async function run(outFile, filter) {
  const redis = createClient({ url: need("REF_REDIS_URL") });
  await redis.connect();
  if ((await redis.dbSize()) > 0 && !(await redis.exists("refdiff:scratch"))) throw new Error("refusing to flush: not a scratch store");
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const scenarios = [];
  for (const [name, make] of Object.entries(SCENARIOS)) {
    if (filter && !name.includes(filter)) continue;
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await sleep(2500); // let a tick in progress finish against the empty stores
    seq = 0;
    const s = make();
    const tickets = [...(s.q1 ?? []), ...(s.q2 ?? [])];
    const players = [...new Set(tickets.flatMap((t) => t.players.map((p) => p.id)))];
    const nowMs = Date.now();
    const multi = redis.multi();
    for (const id of players) {
      const n = Number(id.slice(-4));
      multi.hSet(`player:${id}`, { character: `character_c${n}`, skin: `skin_c${n}_default`, ...(s.noIp?.includes(n) ? {} : { ip: IP(n) }) });
      if (s.heartbeats !== "none") {
        const ago = s.heartbeats ? s.heartbeats[n] : 0;
        if (ago !== undefined) multi.zAdd("player_heartbeats", { score: nowMs - ago * 1000, value: id });
      }
    }
    for (const [n, blocked] of Object.entries(s.blocks ?? {})) multi.set(`player:${P(Number(n))}:blocked`, JSON.stringify(blocked.map(P)));
    for (const t of s.q1 ?? []) multi.rPush("1v1", JSON.stringify(t));
    for (const t of s.q2 ?? []) multi.rPush("2v2", JSON.stringify(t));
    recording = [];
    await multi.exec();
    await sleep(s.wait ?? 5000);
    const lines = recording.filter((l) => l.match(/\[\d+ ([^\]]+)\]/)?.[1] !== self);
    recording = null;
    scenarios.push({ name, ...(await capture(redis, lines, tickets)) });
    process.stdout.write(`${name}: ${scenarios.at(-1).published.length} published\n`);
  }
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ scenarios }, null, 1));
  console.log(`${scenarios.length} scenarios -> ${outFile}`);
  await redis.quit();
}

// The TS worker publishes matchmaking:complete and its websocket builds the message (handleMatchMakingComplete,
// websocket.ts, branch infinity-war: the same message to each player named); C# sends that message through ws:send. No
// websocket runs here, so the TS publish is turned into what that handler sends, and the two compare as ws:send.
function asSent(channel, message) {
  if (channel !== "matchmaking:complete") return { channel, message };
  const payload = { result: { id: message.resultId }, match: { id: message.containerMatchId } };
  if (message.matchmakingRequestId !== undefined) payload.id = message.matchmakingRequestId;
  payload.state = 2;
  return { channel: "ws:send", message: { playerIds: message.playerIds, message: { data: {}, payload, header: "Matchmaking request completed!", cmd: "matchmaking-complete" } } };
}

const WRITES = new Set(["set", "setex", "psetex", "del", "unlink", "lrem", "rpush", "lpush", "hset", "zadd", "zrem", "incr", "expire", "publish"]);

async function capture(redis, lines, seeded) {
  const byParty = new Map(seeded.map((t) => [t.partyId, t]));
  const ids = new Map();
  const writes = [], published = [];
  for (const line of lines) {
    const parts = [...line.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1].replace(/\\(.)/g, "$1"));
    const cmd = parts[0]?.toLowerCase();
    // A C# service's heartbeat into the instance registry lands in any scenario while it runs: not the worker's work.
    if (!WRITES.has(cmd) || parts[1]?.startsWith("matchmaking:lock:") || parts[1]?.startsWith("ovs:instance") || parts[1] === "refdiff:scratch") continue;
    if (cmd === "publish") {
      published.push(asSent(parts[1], JSON.parse(parts[2])));
      continue;
    }
    // A removed ticket by its party; a SET with its expiry (seconds), whatever the client's spelling.
    if (cmd === "lrem") writes.push(`lrem ${parts[1]} ${JSON.parse(parts[3]).partyId}`);
    else if (cmd === "setex") writes.push(`set ${parts[1]} EX ${parts[2]}`);
    else if (cmd === "set") writes.push(`set ${parts[1]}${parts[3] ? ` ${parts[3].toUpperCase()} ${parts[4]}` : ""}`);
    else writes.push(`${cmd} ${parts[1]}`);
  }
  const state = {};
  for await (const key of redis.scanIterator({ COUNT: 1000 })) {
    if (key === "refdiff:scratch" || key.startsWith("matchmaking:lock:") || key.startsWith("ovs:instance")) continue;
    const type = await redis.type(key);
    const ttl = await redis.ttl(key);
    let value;
    if (type === "string") { const raw = await redis.get(key); try { value = JSON.parse(raw); } catch { value = raw; } }
    else if (type === "list") value = (await redis.lRange(key, 0, -1)).map((t) => JSON.parse(t).partyId);
    else if (type === "hash") value = await redis.hGetAll(key);
    else if (type === "zset") value = (await redis.zRange(key, 0, -1)).length + " members";
    else value = type;
    state[key] = { type, ttl: ttl > 0 ? `~${Math.round(ttl / 60)}m` : ttl, value };
  }
  const out = canon({ writes, published, state: Object.fromEntries(Object.entries(state).sort()) }, byParty);
  return rename(out, ids);
}

// The match's random parts, checked and then replaced.
function canon(record, byParty) {
  // Each player's ip is the one in player:{id} (none when that has none).
  const ipOf = (id) => record.state[`player:${id}`]?.value?.ip;
  const checkPlayers = (players, mode) => {
    const problems = [];
    const total = players.length;
    for (const [partyId, t] of byParty) {
      const teams = new Set(players.filter((p) => p.partyId === partyId).map((p) => p.teamIndex));
      if (teams.size > 1) problems.push(`party ${partyId} split`);
      for (const tp of t.players) {
        const p = players.find((x) => x.playerId === tp.id);
        if (p && p.ip !== ipOf(tp.id)) problems.push(`${tp.id} ip ${p.ip}`);
      }
    }
    for (const team of [0, 1]) {
      const idx = players.filter((p) => p.teamIndex === team).map((p) => p.playerIndex).sort((a, b) => a - b);
      if (idx.length !== total / 2) problems.push(`team ${team} has ${idx.length}`);
      if (idx.some((v, i) => v !== i * 2 + team)) problems.push(`team ${team} indexes ${idx}`);
    }
    if (players.filter((p) => p.isHost === true).length !== 1) problems.push(`hosts ${players.filter((p) => p.isHost).length}`);
    if (players.some((p) => "isBot" in p)) problems.push("isBot present");
    const teamsOf = [0, 1].map((team) => [...new Set(players.filter((p) => p.teamIndex === team).map((p) => p.partyId))].sort()).sort();
    return {
      check: problems.length ? problems : "ok",
      teams: teamsOf,
      players: players.map(({ teamIndex, playerIndex, isHost, ...rest }) => ({ ...rest, keys: Object.keys({ teamIndex, playerIndex, isHost, ...rest }).sort().join(",") }))
        .sort((a, b) => a.playerId.localeCompare(b.playerId)),
    };
  };
  const notification = (n) => {
    if (!n || !Array.isArray(n.players)) return n;
    const c = { ...n, players: checkPlayers(n.players, n.mode) };
    if (typeof n.map === "string" && MAPS[n.mode]?.includes(n.map)) c.map = "<map>";
    if (typeof n.matchKey === "string" && Buffer.from(n.matchKey, "base64").length === 32) c.matchKey = "<match key>";
    if (Number.isInteger(n.rollbackPort) && n.rollbackPort >= LOW && n.rollbackPort < HIGH) c.rollbackPort = "<port>";
    return c;
  };
  for (const p of record.published) if (p.channel === "match:notifications") p.message = notification(p.message);
  for (const [key, v] of Object.entries(record.state)) {
    if (v.value?.matchKey) v.value = notification(v.value);
    if (key.startsWith("ranked_set:")) v.value = { ...v.value, players: checkPlayers(v.value.players) };
    if (key.startsWith("match:") && Number.isInteger(v.value?.rollbackPort) && v.value.rollbackPort >= LOW && v.value.rollbackPort < HIGH) v.value.rollbackPort = "<port>";
  }
  return record;
}

// New ids in order of first appearance (the harness's own start 0000); times near now.
function rename(record, ids) {
  const now = Date.now();
  const s = JSON.stringify(record)
    .replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/"?\b1\d{12}\b"?/g, (n) => (Math.abs(Number(n.replaceAll('"', "")) - now) < 120000 ? '"<now ms>"' : n))
    .replace(/"?\b1\d{9}\b"?/g, (n) => (Math.abs(Number(n.replaceAll('"', "")) * 1000 - now) < 120000 ? '"<now s>"' : n));
  return JSON.parse(s);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// The deliberate differences, wherever a match was made: the port, and the set keys' lifetime. The TS worker took INCR
// rollback:current_port even with fixed rollback servers (a port none of them listens on); the port takes one of the
// fixed servers' ports. The set keys (ranked_set, player_ranked_set) live 20 min where TS gave 10, which a game could
// outlast before the websocket wrote them again. With the TS ports put in range, its INCR left out and both sides' set
// TTLs checked and replaced, nothing else may differ.
const setTtl = (record, ex, minutes) => {
  const isSetKey = (key) => key.startsWith("ranked_set:") || key.startsWith("player_ranked_set:");
  // (A renamed id has spaces: "set ranked_set:<new id 1> EX 600".)
  record.writes = record.writes.map((w) => w.replace(new RegExp(`^set ((?:player_)?ranked_set:.+) EX ${ex}$`), "set $1 EX <set ttl>"));
  for (const [key, v] of Object.entries(record.state)) if (isSetKey(key) && v.ttl === `~${minutes}m`) v.ttl = "<set ttl>";
};
const portOnly = {
  why: "the rollback port is one of the fixed servers'; the TS worker counted up rollback:current_port. The set keys live 20 min (TS: 10)",
  holds: (ts, cs) => {
    const tsPorts = [], csPorts = [];
    const fix = (record, ports) => JSON.parse(JSON.stringify(record, (k, v) => {
      if (k === "rollbackPort") { ports.push(v); return "<port>"; }
      return v;
    }));
    const t = fix(ts, tsPorts), c = fix(cs, csPorts);
    t.writes = t.writes.filter((w) => w !== "incr rollback:current_port");
    delete t.state["rollback:current_port"];
    setTtl(t, 600, 10);
    setTtl(c, 1200, 20);
    return tsPorts.length > 0 && tsPorts.every((v) => Number.isInteger(v) && (v < LOW || v >= HIGH)) && csPorts.every((v) => v === "<port>")
      && JSON.stringify(t) === JSON.stringify(c);
  },
};
const EXPECTED = new Proxy({}, { get: (_, name) => (typeof name === "string" && !NO_MATCH.has(name) ? portOnly : undefined) });
// The scenarios where no match is made (no port, so no difference).
const NO_MATCH = new Set(["1v1-skill-apart-young", "1v1-stricter-range-wins", "1v1-blocked-by-the-other", "1v1-lone-silent-ticket-stays",
  "1v1-party-of-two-waits", "2v2-three-players", "2v2-skill-apart-young"]);

// Which parties share a team is random (once the checks above pass): a team is compared by its parties' sizes.
function teamShapes(record) {
  return JSON.parse(JSON.stringify(record, (k, v) => {
    if (v && typeof v === "object" && Array.isArray(v.teams) && Array.isArray(v.players) && "check" in v) {
      const size = (party) => v.players.filter((p) => p.partyId === party).length;
      return { ...v, teams: v.teams.map((team) => team.map(size).sort()).sort() };
    }
    return v;
  }));
}

function diffRuns(fileA, fileB) {
  const read = (f) => { const r = JSON.parse(fs.readFileSync(f, "utf8")); r.scenarios = r.scenarios.map(teamShapes); return r; };
  const a = read(fileA), b = read(fileB);
  let differing = 0;
  for (const name of new Set([...a.scenarios.map((s) => s.name), ...b.scenarios.map((s) => s.name)])) {
    const x = a.scenarios.find((s) => s.name === name), y = b.scenarios.find((s) => s.name === name);
    const parts = ["writes", "published", "state"].filter((k) => JSON.stringify(x?.[k]) !== JSON.stringify(y?.[k]));
    const expected = EXPECTED[name];
    if (!parts.length) {
      if (expected) { differing++; console.log(`${name}: the same on both, but a difference is expected (${expected.why})`); }
      continue;
    }
    if (expected?.holds(x, y)) { console.log(`${name}: differs in ${parts.join(", ")} (expected: ${expected.why})`); continue; }
    if (expected) console.log(`${name}: NOT the expected difference (${expected.why})`);
    differing++;
    console.log(`${name}: differs in ${parts.join(", ")}`);
    for (const k of parts) {
      console.log(`  ${k} A: ${JSON.stringify(x?.[k]).slice(0, 2500)}`);
      console.log(`  ${k} B: ${JSON.stringify(y?.[k]).slice(0, 2500)}`);
    }
  }
  console.log(differing ? `${differing} scenario(s) differ` : "no differences");
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: matchmaker_diff.mjs run <out.json> [filter] | diff <a.json> <b.json>");
  process.exit(2);
}
