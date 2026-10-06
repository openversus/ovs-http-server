// A game that goes away, and what that does to its player's lobbies: the TS websocket's close (src/websocket.ts
// handleDisconnect) against the C# gateway with the lobbies service's reader of its disconnects
// (Core/Realtime/LobbyDisconnects.cs). Each step seeds a lobby, connects the fake games, drops one, and records every
// Redis write from the drop on (MONITOR: commands run by scripts included), the frames each remaining game was sent
// (raw bytes, the date masked), and the lobby keys at the end. Run from the repository root (it uses the TS server's
// node_modules):
//
//   node dotnet/tools/realtime/disconnect_diff.mjs run ts <out.json> [step]   the TS websocket (REF_TS_WS)
//   node dotnet/tools/realtime/disconnect_diff.mjs run cs <out.json> [step]   the C# gateway (REF_CS_WS), with the C#
//                                                                             lobbies service on the same stores
//   node dotnet/tools/realtime/disconnect_diff.mjs diff <ts.json> <cs.json>
//
// Scratch Redis and Mongo, wiped before every step (REF_REDIS_URL, REF_MONGO_URI), and the servers' token secret
// (REF_JWT_SECRET). The TS websocket must be PR #49's code as committed, run from its build as prod runs it. Lobby ids
// either server makes become <id1>, <id2>, ... in order of appearance.
//
// The lobbies' writes are compared in order (the C# reader keeps the TS order); the rest as multisets (the gateway takes
// the player offline at the close, TS at the end of its cleanup). Every other difference is in EXPECTED, which asserts
// it, or a failure.
import fs from "node:fs";
import { require, need, openScratch, openMonitor, canonicalWrite } from "../refdiff/refdiff.mjs";
import { initFrame } from "../refdiff/gateway.mjs";

const WebSocket = require(process.cwd() + "/node_modules/ws");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const oid = (n) => "00000000000000000028" + String(n).padStart(4, "0");
const [P1, P2] = [1, 2].map(oid);
const LOBBY = oid(101), CUSTOM = oid(201);
// The players' address, and a neighbour's whose key starts with it (another household's copy of a session).
const IP = "198.51.100.2", NEIGHBOUR_IP = "198.51.100.25";
const claims = (pid, n) => ({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const SETTLE = 400;
// The C# reader acts a hop later (the stream, read every 50 ms): both sides are given the same time after the drop.
const AFTER_DROP = 1000;

// A session as /access leaves it; its jwt is the token the game sends (the C# reader compares the two).
// With what a login and a lobby leave of the player's: the session's cosmetics copy, the player's records (a lobby record
// among them).
async function seedSession(redis, pid, n) {
  const token = jwt.sign(claims(pid, n), need("REF_JWT_SECRET"));
  await redis.hSet(`connections:${pid}`, { ...claims(pid, n), jwt: token, GameplayPreferences: "448", character: "character_shaggy", skin: "skin_shaggy_default" });
  await redis.hSet(`connections:${pid}:cosmetics`, { Banner: '"banner_default"' });
  await redis.hSet(`player:${pid}`, { character: "character_shaggy", skin: "skin_shaggy_default", ip: IP });
  await redis.set(`player:${pid}:cosmetics`, JSON.stringify({ Banner: "banner_default" }));
  await redis.set(`player:${pid}:blocked`, "[]");
  await redis.hSet(`player:${pid}:lobby:${oid(150 + n)}`, { id: oid(150 + n), created_at: "2026-10-01T10:00:00.000Z", mode: "1v1", owner: pid });
  await redis.sAdd("online_players", pid);
  return token;
}

// The IP's copy of a session (/access, for old clients) as the last login at that address left it, and a neighbour's.
async function seedIpCopies(redis, owner) {
  await redis.hSet(`connections:${IP}`, { id: owner, username: "copy" });
  await redis.hSet(`connections:${NEIGHBOUR_IP}`, { id: oid(99), username: "neighbour" });
}

const partyLobby = (owner, players) => JSON.stringify({ lobbyId: LOBBY, ownerId: owner, ownerUsername: "Player1", mode: players.length > 1 ? "2v2" : "1v1", playerIds: players, createdAt: 1790000000123 });

async function seedParty(redis, owner, players) {
  await redis.set(`lobby:${LOBBY}`, partyLobby(owner, players), { EX: players.length > 1 ? 28800 : 3600 });
  for (const p of players) await redis.set(`player_lobby:${p}`, LOBBY, { EX: 28800 });
}

const member = (id, team, at) => [id, { Account: { id }, JoinedAt: at, BotSettingSlug: "", LobbyPlayerIndex: 0, CrossplayPreference: 1 }];
async function seedCustom(redis, pointers) {
  const teams = [0, 1, 2, 3, 4].map((t) => ({ TeamIndex: t, Players: {}, Length: 0 }));
  for (const [id, player, team] of [[...member(P1, 0, "2026-10-01T10:00:00.000Z"), 0], [...member(P2, 1, "2026-10-01T10:00:01.000Z"), 1]]) {
    teams[team].Players[id] = player;
    teams[team].Length++;
  }
  await redis.set(`custom_lobby_ssc:${CUSTOM}`, JSON.stringify({
    Teams: teams, LeaderID: P1, LobbyType: 0, ReadyPlayers: { [P1]: true, [P2]: true },
    PlayerGameplayPreferences: { [P1]: 448, [P2]: 448 }, PlayerAutoPartyPreferences: { [P1]: false, [P2]: false },
    Platforms: { [P1]: "PC", [P2]: "PC" }, LockedLoadouts: {}, IsLobbyJoinable: true, MatchID: CUSTOM, GameModeSlug: "gm_classic_2v2",
    match_config: { TeamStyle: "Duos", NumRingoutsForWin: 4, MatchDuration: 420, AllowHazards: true, EnableShields: 1 },
    Maps: [{ Map: "M001", IsSelected: true }], WorldBuffs: [], PlayerBuffs: {}, Handicaps: {},
  }), { EX: 172800 });
  if (pointers) for (const p of [P1, P2]) await redis.set(`ssc_custom_lobby_player:${p}`, CUSTOM, { EX: 172800 });
}

const STEPS = {
  // A player alone in their party lobby: the lobby goes (redisCleanupPlayerLobby).
  async "solo-party"(c) {
    const t1 = await seedSession(c.redis, P1, 1);
    await seedIpCopies(c.redis, P1);
    await seedParty(c.redis, P1, [P1]);
    await c.connect({ P1: t1 });
    await c.drop("P1");
  },
  // The owner of a party of two: the lobby goes; the other is told and gets a solo lobby to join.
  async "party-owner-drops"(c) {
    const t1 = await seedSession(c.redis, P1, 1), t2 = await seedSession(c.redis, P2, 2);
    await seedIpCopies(c.redis, P1);
    await seedParty(c.redis, P1, [P1, P2]);
    await c.redis.sAdd(`party_ready:${LOBBY}`, P2);
    await c.connect({ P1: t1, P2: t2 });
    await c.drop("P1");
  },
  // The other one: the owner keeps the lobby, alone.
  async "party-member-drops"(c) {
    const t1 = await seedSession(c.redis, P1, 1), t2 = await seedSession(c.redis, P2, 2);
    // The other player logged in at the same address after the one who drops: the IP's copy is theirs.
    await seedIpCopies(c.redis, P1);
    await seedParty(c.redis, P1, [P1, P2]);
    await c.redis.sAdd(`party_ready:${LOBBY}`, P1);
    await c.connect({ P1: t1, P2: t2 });
    await c.drop("P2");
  },
  // A custom lobby's guest (with a solo party lobby too): out of the custom lobby, the leader told; then the party lobby.
  async "custom-guest-drops"(c) {
    const t1 = await seedSession(c.redis, P1, 1), t2 = await seedSession(c.redis, P2, 2);
    await seedIpCopies(c.redis, P2);
    await seedCustom(c.redis, true);
    await c.redis.set(`lobby:${LOBBY}`, partyLobby(P2, [P2]), { EX: 3600 });
    await c.redis.set(`player_lobby:${P2}`, LOBBY, { EX: 28800 });
    await c.connect({ P1: t1, P2: t2 });
    await c.drop("P2");
  },
  // The same with the pointers gone (they live 20 minutes from a match's start): the lobby is found all the same.
  async "custom-guest-drops-no-pointer"(c) {
    const t1 = await seedSession(c.redis, P1, 1), t2 = await seedSession(c.redis, P2, 2);
    await seedIpCopies(c.redis, P2);
    await seedCustom(c.redis, false);
    await c.connect({ P1: t1, P2: t2 });
    await c.drop("P2");
  },
};

/** A fake game: connects as the proxy passes it on, sends its first frame, answers pings, keeps every frame as hex. */
function game(url, first) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url, { headers: { "x-forwarded-for": IP } });
    const g = { ws, frames: [], close: null };
    ws.on("open", () => { ws.send(first); resolve(g); });
    ws.on("message", (data) => {
      const bytes = Buffer.from(data);
      if (bytes.length === 1 && bytes[0] === 0x0c) { ws.send(Buffer.from([0x0a])); return; }
      g.frames.push(bytes.toString("hex"));
    });
    ws.on("close", (code, reason) => { g.close = { code, reason: reason.toString() }; });
    ws.on("error", reject);
  });
}

const LOBBY_KEYS = /^(lobby|player_lobby|pending_join_lobby|party_ready|custom_lobby_ssc|ssc_custom_lobby_player|lobby_code|lobby_redirect):|^player:[0-9a-f]{24}:lobby:/;
const WRITES = new Set(["set", "setex", "psetex", "expire", "pexpire", "publish", "del", "unlink", "hset", "hmset", "hdel", "sadd", "srem", "zadd", "zrem", "zremrangebyscore", "lpush", "rpush", "lrem", "xadd", "incr"]);
function writesOf(lines, self) {
  return lines
    .filter((line) => line.match(/\[\d+ ([^\]]+)\]/)?.[1] !== self)
    .map((line) => [...line.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]))
    .filter((parts) => parts.length && WRITES.has(parts[0].toLowerCase()) && !/^(refdiff:scratch|ovs:instance(s$|:)|realtime:)/.test(parts[1] ?? ""))
    .map(canonicalWrite)
    // One DEL of several keys (TS deletes each SCAN page at once) as one DEL per key, in key order.
    .flatMap((parts) => (parts[0] === "del" && parts.length > 2 ? parts.slice(1).sort().map((k) => ["del", k]) : [parts]))
    .map((parts) => parts.join(" "));
}

async function state(redis) {
  const out = {};
  for (const pattern of ["lobby:*", "player_lobby:*", "pending_join_lobby:*", "party_ready:*", "custom_lobby_ssc:*", "ssc_custom_lobby_player:*", "player:*", "connections:*"]) {
    for (const key of (await redis.keys(pattern)).sort()) {
      const type = await redis.type(key);
      out[key] = type === "string" ? await redis.get(key) : type === "set" ? (await redis.sMembers(key)).sort() : type === "hash" ? Object.keys(await redis.hGetAll(key)).sort() : type;
    }
  }
  out.online_players = (await redis.sMembers("online_players")).sort();
  return out;
}

async function run(side, outFile, only) {
  const url = need(side === "ts" ? "REF_TS_WS" : "REF_CS_WS");
  const steps = {};
  for (const [name, step] of Object.entries(STEPS)) {
    if (only && name !== only) continue;
    const { redis, close } = await openScratch("disconnect_diff");
    const lines = [];
    const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => lines.push(line));
    const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
    const games = {};
    let mark = null, from = 0, to = 0;
    const c = {
      redis,
      async connect(tokens) {
        for (const [k, token] of Object.entries(tokens)) games[k] = await game(url, initFrame(token, Number(k.slice(1))));
        await sleep(SETTLE);
        for (const g of Object.values(games)) g.frames.length = 0;
      },
      async drop(k) {
        mark = lines.length;
        from = Math.floor(Date.now() / 1000);
        games[k].ws.terminate();
        await sleep(AFTER_DROP);
        to = Math.ceil(Date.now() / 1000);
        games[k].dropped = true;
      },
    };
    let end, after;
    try {
      await step(c);
    } finally {
      // Read before the other games go: their closes clean up too.
      end = lines.length;
      after = await state(redis);
      for (const g of Object.values(games)) g.ws.terminate();
    }
    // The PlayerLeftLobby's JoinedAt is the drop's time: a Hydra date is 4 bytes of seconds, masked.
    const mask = (hex) => { for (let t = from - 2; t <= to + 2; t++) hex = hex.replaceAll(t.toString(16).padStart(8, "0"), "<date>"); return hex; };
    steps[name] = {
      writes: writesOf(lines.slice(mark ?? end, end), self),
      frames: Object.fromEntries(Object.entries(games).filter(([, g]) => !g.dropped).map(([k, g]) => [k, g.frames.map(mask)])),
      state: after,
    };
    monitor.destroy();
    await close();
    console.log(`${side} ${name}: ${steps[name].writes.length} writes; ${Object.entries(steps[name].frames).map(([k, f]) => `${k} ${f.length} frames`).join(", ")}`);
  }
  fs.writeFileSync(outFile, JSON.stringify({ side, ranAt: new Date().toISOString(), steps }, null, 1));
}

// New lobby ids (any 24-hex id but the harness's own) as <id1>, <id2>, ... by first appearance; times as <ms> / <iso>.
function normalize(record) {
  const ids = new Map();
  const fix = (s) => s
    .replace(/\b(?!00000000000000000028)[0-9a-f]{24}\b/g, (id) => { if (!ids.has(id)) ids.set(id, `<id${ids.size + 1}>`); return ids.get(id); })
    .replace(/\b1\d{12}\b/g, "<ms>")
    .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z/g, (iso) => (iso.startsWith("2026-10-01T10:00:0") ? iso : "<iso>"));
  return JSON.parse(fix(JSON.stringify(record)));
}

// Differences that are the design, each asserted: the check must hold on the records, and adjust takes them out of both.
const has = (list, re) => list.some((w) => re.test(w));
const take = (list, re) => { const i = list.findIndex((w) => re.test(w)); if (i >= 0) list.splice(i, 1); return i >= 0; };
const takeAll = (list, re) => { let n = 0; while (take(list, re)) n++; return n; };

// The session keys, compared; the IP's copy goes by its exact key in C# (TS: connections:{ip}*, a neighbour's too).
const NEIGHBOUR = {
  what: "TS deletes the IP's copy by pattern (connections:{ip}*), which took the neighbouring address's copy too (198.51.100.25 for 198.51.100.2); C# deletes the one key",
  check: (ts, cs) => has(ts.writes, new RegExp(`^del connections:${NEIGHBOUR_IP}$`)) && !has(cs.writes, new RegExp(`^del connections:${NEIGHBOUR_IP}$`))
    && !(`connections:${NEIGHBOUR_IP}` in ts.state) && `connections:${NEIGHBOUR_IP}` in cs.state,
  adjust: (a, b) => { take(a.writes, new RegExp(`^del connections:${NEIGHBOUR_IP}$`)); for (const r of [a, b]) delete r.state[`connections:${NEIGHBOUR_IP}`]; },
};
// Where the IP's copy is another player's, TS's household check skips the pattern delete: the neighbour's copy stays.
// The others are told PlayerLeftLobby: TS publishes it for its websocket's own relay, C# sends it through ws:send. The
// frames each game was sent are compared, not the publishes.
const TOLD = {
  what: "the PlayerLeftLobby: TS publishes custom_lobby:notification (relayed by its websocket), C# ws:send (delivered by the gateway); the frames compared",
  check: (ts, cs) => ts.writes.filter((w) => w.startsWith("publish custom_lobby:notification ")).length === 1 && !has(ts.writes, /^publish ws:send /)
    && cs.writes.filter((w) => w.startsWith("publish ws:send ")).length === 1 && !has(cs.writes, /^publish custom_lobby:notification /),
  adjust: (a, b) => { take(a.writes, /^publish custom_lobby:notification /); take(b.writes, /^publish ws:send /); },
};
// TS's createLobby writes the whole session back with lobby_id; C# writes the one field (PartyService's header).
const LOBBY_ID = {
  what: "the remaining player's new lobby in their session: TS writes the whole session back (HSET of every field), C# lobby_id alone",
  check: (ts, cs) => ts.writes.some((w) => w.startsWith(`hset connections:${P2} `) && w.includes(" jwt ") && w.includes(" lobby_id "))
    && cs.writes.some((w) => w === `hset connections:${P2} lobby_id <id1>`),
  adjust: (a, b) => { take(a.writes, new RegExp(`^hset connections:${P2} `)); take(b.writes, new RegExp(`^hset connections:${P2} lobby_id `)); },
};
// TS looks for the custom lobby with KEYS on every disconnect and its web custom lobby with a GET: reads, not recorded.
const EXPECTED = {
  "solo-party": [NEIGHBOUR],
  "party-owner-drops": [NEIGHBOUR, TOLD, LOBBY_ID],
  "party-member-drops": [TOLD],
  "custom-guest-drops": [NEIGHBOUR, TOLD],
  "custom-guest-drops-no-pointer": [NEIGHBOUR, TOLD],
};
const DROPPED = { "solo-party": P1, "party-owner-drops": P1, "party-member-drops": P2, "custom-guest-drops": P2, "custom-guest-drops-no-pointer": P2 };

// A remaining game's answer to a ping (either server's tick can fall in the step): its heartbeat and its IP's session
// renewed. Only another player's: the dropped one's would be a difference.
const pong = (w, dropped) => /^(zadd player_heartbeats <ms> |zadd active_ip_accounts:\S+ <ms> )/.test(w) ? !w.endsWith(dropped)
  : /^(zremrangebyscore|expire) active_ip_accounts:/.test(w);

function split(record, dropped) {
  // The lobbies' writes in order; the rest (presence, sessions) as a multiset.
  const lobby = [], rest = [];
  for (const w of record.writes) (LOBBY_KEYS.test(w.split(" ")[1] ?? "") ? lobby : pong(w, dropped) ? [] : rest).push(w);
  return { lobbyWrites: lobby, otherWrites: rest.sort(), frames: record.frames, state: record.state };
}

function diff(tsFile, csFile) {
  const ts = JSON.parse(fs.readFileSync(tsFile, "utf8")).steps;
  const cs = JSON.parse(fs.readFileSync(csFile, "utf8")).steps;
  let failed = 0;
  for (const name of Object.keys(STEPS)) {
    if (!ts[name] || !cs[name]) continue;
    const tsN = normalize(ts[name]), csN = normalize(cs[name]);
    const a = structuredClone(tsN), b = structuredClone(csN);
    for (const e of EXPECTED[name] ?? []) {
      let ok;
      try { ok = e.check(tsN, csN, DROPPED[name]); } catch (err) { ok = false; console.log(`  (${err.message})`); }
      console.log(`${ok ? "asserted" : "ASSERTION FAILED"} ${name}: ${e.what}`);
      if (!ok) failed++;
      e.adjust(a, b, DROPPED[name]);
    }
    const differences = compare(split(a, DROPPED[name]), split(b, DROPPED[name]), name);
    for (const d of differences) console.log(`DIFF ${d}`);
    if (differences.length === 0) console.log(`same ${name}`);
    failed += differences.length;
  }
  process.exit(failed ? 1 : 0);
}

function compare(a, b, path) {
  if (JSON.stringify(a) === JSON.stringify(b)) return [];
  if (a && b && typeof a === "object" && typeof b === "object" && !Array.isArray(a) && !Array.isArray(b)) {
    return [...new Set([...Object.keys(a), ...Object.keys(b)])].flatMap((k) => compare(a[k], b[k], `${path}.${k}`));
  }
  return [`${path}:\n   ts ${JSON.stringify(a)}\n   cs ${JSON.stringify(b)}`];
}

const [mode, ...rest] = process.argv.slice(2);
if (mode === "run") await run(rest[0], rest[1], rest[2]);
else if (mode === "diff") diff(rest[0], rest[1]);
else console.log("run ts|cs <out.json> [step] | diff <ts.json> <cs.json>");
