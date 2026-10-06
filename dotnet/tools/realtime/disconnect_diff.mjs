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
//   node dotnet/tools/realtime/disconnect_diff.mjs run reap <out.json> [step] the C# gateway again, but the game that
//                                                                             drops is held by a second node (REF_GW_B_WS,
//                                                                             started for each step by REF_GW_B_CMD), which
//                                                                             is killed (SIGKILL) instead: the first node
//                                                                             reaps it (GatewayReaper)
//   node dotnet/tools/realtime/disconnect_diff.mjs diff-reap <cs.json> <reap.json>
//
// Scratch Redis and Mongo, wiped before every step (REF_REDIS_URL, REF_MONGO_URI), and the servers' token secret
// (REF_JWT_SECRET). The TS websocket must be PR #49's code as committed, run from its build as prod runs it. Lobby ids
// either server makes become <id1>, <id2>, ... in order of appearance.
//
// The match steps (match-*, set-*) announce a ranked match first, as each server is told of one (TS: match:notifications,
// to its websocket; C#: match:launched, to the match flow, which needs Realtime:Gateway on), so that each game holds its
// config when it drops (the TS websocket's matchConfig, C#'s match_config:{player}); each run records which games got
// one. They need the C# match flow on the stores for the C# run, and nothing C# but the gateway's lobbies and match flow
// readers: the match flow's bridge (GameplayConfigBridge) would build configs from the TS run's match:notifications.
// Their frames are compared decoded (the FullRankUpdate's season: TS's is always Season:SeasonFive, C#'s the current
// one), with the ratings after (eloratings).
//
// The lobbies' writes are compared in order (the C# reader keeps the TS order); the rest as multisets (the gateway takes
// the player offline at the close, TS at the end of its cleanup). Every other difference is in EXPECTED, which asserts
// it, or a failure.
//
// A reaped game (no reference: TS ran no close at all for a websocket that crashed) is compared with the same game
// closing its socket on the C# gateway: the same writes and state for its lobbies, its queue and its session, and for
// its match the server's failure instead of a leave (REAP_EXPECTED). The nodes need short timings for it (Gateway:
// PingIntervalMs 1000, ReapAfterMs 3000, ReapIntervalMs 1000); the dead node's registry entry is left to run out (20 s).
import fs from "node:fs";
import net from "node:net";
import { spawn } from "node:child_process";
import { require, need, openScratch, openMonitor, canonicalWrite } from "../refdiff/refdiff.mjs";
import { initFrame, decodeFrame } from "../refdiff/gateway.mjs";

const WebSocket = require(process.cwd() + "/node_modules/ws");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const oid = (n) => "00000000000000000028" + String(n).padStart(4, "0");
const [P1, P2] = [1, 2].map(oid);
const LOBBY = oid(101), CUSTOM = oid(201), MATCH = oid(301), SET = oid(302), SPEC = oid(4);
// The players' address, and a neighbor's whose key starts with it (another household's copy of a session).
const IP = "198.51.100.2", NEIGHBOR_IP = "198.51.100.25";
const claims = (pid, n) => ({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const SETTLE = 400;
// The C# reader acts a hop later (the stream, read every 50 ms): both sides are given the same time after the drop.
const AFTER_DROP = 1000;
// A dodge rates the set (Mongo) and sends ranks: more time for those.
const AFTER_MATCH_DROP = 2000;

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

// The IP's copy of a session (/access, for old clients) as the last login at that address left it, and a neighbor's.
async function seedIpCopies(redis, owner) {
  await redis.hSet(`connections:${IP}`, { id: owner, username: "copy" });
  await redis.hSet(`connections:${NEIGHBOR_IP}`, { id: oid(99), username: "neighbor" });
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
  // P2's game goes before the match starts: a pregame dodge (rated: a 1v1 from the queue, game 1 of its set).
  async "match-pregame-dodge"(c) {
    await c.match({ P1: await seedSession(c.redis, P1, 1), P2: await seedSession(c.redis, P2, 2) });
    await c.drop("P2", AFTER_MATCH_DROP);
  },
  // After the start: the set's next check-in concedes it for P2.
  async "match-mid-game"(c) {
    await c.match({ P1: await seedSession(c.redis, P1, 1), P2: await seedSession(c.redis, P2, 2) });
    await c.redis.set(`match_started:${MATCH}`, "1", { EX: 600 });
    await c.drop("P2", AFTER_MATCH_DROP);
  },
  // After the game's result, in a set that goes on: nothing for the match, the set flagged.
  async "match-after-result"(c) {
    await c.match({ P1: await seedSession(c.redis, P1, 1), P2: await seedSession(c.redis, P2, 2) });
    await c.redis.set(`game_result_received:${MATCH}`, "1", { EX: 600 });
    for (const p of [P1, P2]) await c.redis.set(`player_ranked_set:${p}`, SET, { EX: 1200 });
    await c.drop("P2", AFTER_MATCH_DROP);
  },
  // The match's spectator's game goes before the start.
  async "match-spectator"(c) {
    await c.match({ P1: await seedSession(c.redis, P1, 1), P2: await seedSession(c.redis, P2, 2), P4: await seedSession(c.redis, SPEC, 4) }, true);
    await c.drop("P4", AFTER_MATCH_DROP);
  },
  // Between a set's games (no match held): the set flagged.
  async "set-between-games"(c) {
    const t1 = await seedSession(c.redis, P1, 1), t2 = await seedSession(c.redis, P2, 2);
    for (const p of [P1, P2]) await c.redis.set(`player_ranked_set:${p}`, SET, { EX: 1200 });
    await c.connect({ P1: t1, P2: t2 });
    await c.drop("P2", AFTER_MATCH_DROP);
  },
};

// A ranked 1v1 as the matchmaker launches it (the notification), with a spectator when asked.
const human = (playerId, playerIndex, teamIndex) => ({ playerId, partyId: MATCH, playerIndex, teamIndex, isHost: playerIndex === 0, ip: IP, isBot: false });
const matchNotification = (spectator) => ({
  players: [human(P1, 0, 0), human(P2, 1, 1), ...(spectator ? [{ playerId: SPEC, partyId: MATCH, playerIndex: 8888, teamIndex: -1, isHost: false, ip: IP, isSpectator: true }] : [])],
  matchId: MATCH, matchKey: "the-match-key", map: "M001_V2", mode: "1v1", rollbackPort: 57003, p2p: false,
});

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
  for (const pattern of ["lobby:*", "player_lobby:*", "pending_join_lobby:*", "party_ready:*", "custom_lobby_ssc:*", "ssc_custom_lobby_player:*", "player:*", "connections:*",
    "dll_notifications:*", "ranked_disconnect:*", "elo_processed*", "player_ranked_set:*", "ranked_set*"]) {
    for (const key of (await redis.keys(pattern)).sort()) {
      const type = await redis.type(key);
      out[key] = type === "string" ? await redis.get(key) : type === "set" ? (await redis.sMembers(key)).sort() : type === "hash" ? Object.keys(await redis.hGetAll(key)).sort()
        : type === "list" ? await redis.lRange(key, 0, -1) : type;
      // A flag's life is part of it (the TS close wrote ranked_disconnect for 10 minutes, then 2).
      if (key.startsWith("ranked_disconnect:")) out[key] = { value: out[key], ttl: `~${Math.round((await redis.ttl(key)) / 60)}m` };
    }
  }
  out.online_players = (await redis.sMembers("online_players")).sort();
  return out;
}

// The second gateway node, for a reap step: started, and up once its port answers.
async function startNodeB() {
  const child = spawn("bash", ["-c", need("REF_GW_B_CMD")], { detached: true, stdio: "ignore" });
  const { hostname, port } = new URL(need("REF_GW_B_WS"));
  for (let i = 0; i < 240; i++) {
    const up = await new Promise((resolve) => {
      const socket = net.connect(Number(port), hostname, () => { socket.destroy(); resolve(true); });
      socket.on("error", () => resolve(false));
    });
    if (up) return child;
    await sleep(250);
  }
  throw new Error(`the second gateway node did not come up on ${port}`);
}

const killNode = (child) => { try { process.kill(-child.pid, "SIGKILL"); } catch { /* gone already */ } };

async function run(side, outFile, only) {
  const url = need(side === "ts" ? "REF_TS_WS" : "REF_CS_WS");
  const reap = side === "reap";
  const steps = {};
  for (const [name, step] of Object.entries(STEPS)) {
    if (only && name !== only) continue;
    const nodeB = reap ? await startNodeB() : null;
    const { redis, db, close } = await openScratch("disconnect_diff");
    const lines = [];
    const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => lines.push(line));
    const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
    const games = {};
    let mark = null, from = 0, to = 0, delivered = null, reapedAfterMs;
    // In a reap step the game that drops is the second node's.
    const urlOf = (k) => (reap && oid(Number(k.slice(1))) === DROPPED[name] ? need("REF_GW_B_WS") : url);
    const c = {
      redis,
      async connect(tokens) {
        for (const [k, token] of Object.entries(tokens)) games[k] = await game(urlOf(k), initFrame(token, Number(k.slice(1))));
        await sleep(SETTLE);
        for (const g of Object.values(games)) g.frames.length = 0;
      },
      // The games connect, then the match is announced as each server is told of one; which games got their config.
      async match(tokens, spectator = false) {
        // The players' records, whose names /access copies into their sessions.
        await db.collection("playertesters").insertMany([[P1, 1], [P2, 2], [SPEC, 4]].map(([id, n]) => ({ _id: new ObjectId(id), name: `Player${n}` })));
        await c.connect(tokens);
        const notification = matchNotification(spectator);
        await redis.set(MATCH, JSON.stringify(notification), { EX: 1200 });
        if (side === "ts") await redis.publish("match:notifications", JSON.stringify(notification));
        else await redis.xAdd("match:launched", "*", { match: MATCH, notification: JSON.stringify(notification) });
        await sleep(1500);
        delivered = Object.fromEntries(Object.entries(games).map(([k, g]) => [k, g.frames.some((f) => decodeFrame(Buffer.from(f, "hex"))?.data?.GameplayConfig !== undefined)]));
        for (const g of Object.values(games)) g.frames.length = 0;
      },
      async drop(k, wait = AFTER_DROP) {
        mark = lines.length;
        from = Math.floor(Date.now() / 1000);
        if (reap) {
          // The node dies with the game's socket; the step goes on once the other node has reaped the player.
          const started = Date.now(), entry = `realtime:conn:${oid(Number(k.slice(1)))}`;
          killNode(nodeB);
          while (await redis.exists(entry) && Date.now() - started < 90_000) await sleep(250);
          reapedAfterMs = (await redis.exists(entry)) ? null : Date.now() - started;
        } else {
          games[k].ws.terminate();
        }
        await sleep(wait);
        to = Math.ceil(Date.now() / 1000);
        games[k].dropped = true;
      },
    };
    const matchStep = /^(match|set)-/.test(name);
    let end, after, ratings;
    try {
      await step(c);
    } finally {
      // Read before the other games go: their closes clean up too (and dodge this step's match).
      end = lines.length;
      after = await state(redis);
      ratings = matchStep ? (await db.collection("eloratings").find({}, { sort: { account_id: 1 } }).toArray()).map(({ _id, ...rest }) => rest) : undefined;
      for (const g of Object.values(games)) g.ws.terminate();
      if (nodeB) killNode(nodeB);
      // Their closes are handled too (a dodge of this step's match, in C# a stream read later): before the next step's
      // stores are wiped, not after.
      await sleep(AFTER_MATCH_DROP);
    }
    // The PlayerLeftLobby's JoinedAt is the drop's time: a Hydra date is 4 bytes of seconds, masked.
    const mask = (hex) => { for (let t = from - 2; t <= to + 2; t++) hex = hex.replaceAll(t.toString(16).padStart(8, "0"), "<date>"); return hex; };
    steps[name] = {
      writes: writesOf(lines.slice(mark ?? end, end), self),
      // The lobby steps' frames as bytes; the match steps' decoded (the FullRankUpdate's season is set aside in diff).
      frames: Object.fromEntries(Object.entries(games).filter(([, g]) => !g.dropped)
        .map(([k, g]) => [k, matchStep ? g.frames.map((f) => decodeFrame(Buffer.from(f, "hex"))) : g.frames.map(mask)])),
      state: after,
      ...(delivered ? { delivered } : {}),
      ...(matchStep ? { eloratings: ratings } : {}),
      ...(reap ? { reapedAfterMs } : {}),
    };
    monitor.destroy();
    await close();
    console.log(`${side} ${name}: ${steps[name].writes.length} writes; ${Object.entries(steps[name].frames).map(([k, f]) => `${k} ${f.length} frames`).join(", ")}`
      + (reap ? `; reaped after ${reapedAfterMs ?? "NEVER (90 s)"} ms` : ""));
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
  // Strings and keys as text; a number that is a time (ms, or a Hydra date's seconds) as <ms> / <s>.
  const walk = (v) => Array.isArray(v) ? v.map(walk)
    : v && typeof v === "object" ? Object.fromEntries(Object.entries(v).map(([k, x]) => [fix(k), walk(x)]))
    : typeof v === "string" ? fix(v)
    : typeof v === "number" && /^1\d{12}$/.test(String(v)) ? "<ms>"
    : typeof v === "number" && /^1\d{9}$/.test(String(v)) ? "<s>"
    : v;
  return walk(record);
}

// Differences that are the design, each asserted: the check must hold on the records, and adjust takes them out of both.
const has = (list, re) => list.some((w) => re.test(w));
const take = (list, re) => { const i = list.findIndex((w) => re.test(w)); if (i >= 0) list.splice(i, 1); return i >= 0; };
const takeAll = (list, re) => { let n = 0; while (take(list, re)) n++; return n; };

// The session keys, compared; the IP's copy goes by its exact key in C# (TS: connections:{ip}*, a neighbor's too).
const NEIGHBOR = {
  what: "TS deletes the IP's copy by pattern (connections:{ip}*), which took the neighboring address's copy too (198.51.100.25 for 198.51.100.2); C# deletes the one key",
  check: (ts, cs) => has(ts.writes, new RegExp(`^del connections:${NEIGHBOR_IP}$`)) && !has(cs.writes, new RegExp(`^del connections:${NEIGHBOR_IP}$`))
    && !(`connections:${NEIGHBOR_IP}` in ts.state) && `connections:${NEIGHBOR_IP}` in cs.state,
  adjust: (a, b) => { take(a.writes, new RegExp(`^del connections:${NEIGHBOR_IP}$`)); for (const r of [a, b]) delete r.state[`connections:${NEIGHBOR_IP}`]; },
};
// Where the IP's copy is another player's, TS's household check skips the pattern delete: the neighbor's copy stays.
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
// The match steps (MatchStatusEvents.GameClosedAsync, decided 2026-10-05 for the rollback side's PlayerDisconnect).
// The dodge sets every player idle; the dodger's own close deletes their record. TS did both in one handler (the idle,
// then the delete); in C# the two readers run apart, and the idle is written only while the record is still there.
const DODGER_IDLE = {
  what: "the dodger's idle status: TS wrote it, then deleted their record; C# writes it only while the record is there (the lobbies' cleanup may run first), so either way no record is left",
  check: (ts, cs, dropped) => ts.writes.includes(`hset player:${dropped} status idle`) && cs.writes.filter((w) => w === `hset player:${dropped} status idle`).length <= 1
    && !(`player:${dropped}` in ts.state) && !(`player:${dropped}` in cs.state),
  adjust: (a, b, dropped) => { takeAll(a.writes, new RegExp(`^hset player:${dropped} status idle$`)); takeAll(b.writes, new RegExp(`^hset player:${dropped} status idle$`)); },
};
const RANKS = {
  what: "the ranks after the dodge: TS publishes ranked_set:fullrankupdate (its websocket builds each connected player's), C# sends each player's FullRankUpdate through ws:send (connected or not); the frames compared",
  check: (ts, cs) => ts.writes.filter((w) => w.startsWith("publish ranked_set:fullrankupdate ")).length === 1
    && cs.writes.filter((w) => w.startsWith("publish ws:send ") && w.includes("FullRankUpdate")).length === 2,
  adjust: (a, b) => { take(a.writes, /^publish ranked_set:fullrankupdate /); takeAll(b.writes, /^publish ws:send .*FullRankUpdate/); },
};
const flagOf = (run, dropped) => run.writes.filter((w) => w.startsWith(`set ranked_disconnect:${dropped} `));
const DODGER_FLAG = {
  what: "C# also flags the dodger with the dropped set's id (as its rollback path: the flag names a set that is gone, so it never concedes another); the TS close wrote none",
  check: (ts, cs, dropped) => flagOf(ts, dropped).length === 0 && JSON.stringify(flagOf(cs, dropped)) === JSON.stringify([`set ranked_disconnect:${dropped} ${MATCH} EX 600`]),
  adjust: (a, b, dropped) => { take(b.writes, new RegExp(`^set ranked_disconnect:${dropped} `)); delete b.state[`ranked_disconnect:${dropped}`]; },
};
const FLAG_VALUE = {
  what: "a mid-game leaver's flag: TS wrote \"1\" (any started match), C# the set's id (the match's: game 1), both for 10 minutes; RankedSets concedes only the set it names",
  check: (ts, cs, dropped) => JSON.stringify(flagOf(ts, dropped)) === JSON.stringify([`set ranked_disconnect:${dropped} 1 EX 600`])
    && JSON.stringify(flagOf(cs, dropped)) === JSON.stringify([`set ranked_disconnect:${dropped} ${MATCH} EX 600`]),
  adjust: (a, b, dropped) => { b.writes[b.writes.indexOf(flagOf(b, dropped)[0])] = flagOf(a, dropped)[0]; b.state[`ranked_disconnect:${dropped}`] = a.state[`ranked_disconnect:${dropped}`]; },
};
const FLAG_TTL = {
  what: "the set's flag: TS wrote it for 2 minutes (shorter than a game, so a mid-game leaver's ran out before the check-in that reads it), C# for 10",
  check: (ts, cs, dropped) => JSON.stringify(flagOf(ts, dropped)) === JSON.stringify([`set ranked_disconnect:${dropped} ${SET} EX 120`])
    && JSON.stringify(flagOf(cs, dropped)) === JSON.stringify([`set ranked_disconnect:${dropped} ${SET} EX 600`]),
  adjust: (a, b, dropped) => { b.writes[b.writes.indexOf(flagOf(b, dropped)[0])] = flagOf(a, dropped)[0]; b.state[`ranked_disconnect:${dropped}`] = a.state[`ranked_disconnect:${dropped}`]; },
};
const SPECTATOR = {
  what: "a spectator's game closing before the start: TS took the spectator's team for a dodger's, rated the set (P1 won, P2 lost), dropped it and told both players the match was cancelled; C# changes nothing for the match",
  check: (ts, cs) => ts.state[`elo_processed_set:${MATCH}`] === "pregame_dodge" && !(`elo_processed_set:${MATCH}` in cs.state)
    && ts.eloratings.some((e) => e.account_id === P1 && e.wins_1v1 === 1) && cs.eloratings.every((e) => e.wins_1v1 === 0 && e.losses_1v1 === 0)
    && [P1, P2].every((p) => `dll_notifications:${p}` in ts.state && !(`dll_notifications:${p}` in cs.state)),
  adjust: (a, b) => {
    takeAll(a.writes, /^(set elo_processed|publish ranked_set:fullrankupdate |del player_ranked_set:|hset player:\S+ status idle$|rpush dll_notifications:|expire dll_notifications:)/);
    for (const key of Object.keys(a.state)) if (/^(elo_processed|dll_notifications:)/.test(key)) delete a.state[key];
    for (const p of [P1, P2]) a.state[`player:${p}`] = a.state[`player:${p}`]?.filter((f) => f !== "status");
    a.frames = b.frames;
    a.eloratings = b.eloratings;
  },
};
// TS looks for the custom lobby with KEYS on every disconnect and its web custom lobby with a GET: reads, not recorded.
const EXPECTED = {
  "match-pregame-dodge": [RANKS, DODGER_FLAG, DODGER_IDLE],
  "match-mid-game": [FLAG_VALUE],
  "match-after-result": [FLAG_TTL],
  "match-spectator": [SPECTATOR],
  "set-between-games": [FLAG_TTL],
  "solo-party": [NEIGHBOR],
  "party-owner-drops": [NEIGHBOR, TOLD, LOBBY_ID],
  "party-member-drops": [TOLD],
  "custom-guest-drops": [NEIGHBOR, TOLD],
  "custom-guest-drops-no-pointer": [NEIGHBOR, TOLD],
};
const DROPPED = { "solo-party": P1, "party-owner-drops": P1, "party-member-drops": P2, "custom-guest-drops": P2, "custom-guest-drops-no-pointer": P2,
  "match-pregame-dodge": P2, "match-mid-game": P2, "match-after-result": P2, "match-spectator": SPEC, "set-between-games": P2 };

// FullRankUpdate's season: TS's is always Season:SeasonFive, C#'s Season:Current (decided: as ranked_data and the login).
function seasonless(run, wanted) {
  let ok = true;
  for (const f of Object.values(run.frames ?? {}).flat()) {
    if (f?.data?.template_id !== "FullRankUpdate") continue;
    const keys = Object.keys(f.data.SeasonalData ?? {});
    if (keys.length !== 1 || !wanted(keys[0])) ok = false;
    f.data.SeasonalData = { "<season>": f.data.SeasonalData[keys[0]] };
  }
  return ok;
}

// A remaining game's answer to a ping (either server's tick can fall in the step): its heartbeat and its IP's session
// renewed. Only another player's: the dropped one's would be a difference.
const pong = (w, dropped) => /^(zadd player_heartbeats <ms> |zadd active_ip_accounts:\S+ <ms> )/.test(w) ? !w.endsWith(dropped)
  : /^(zremrangebyscore|expire) active_ip_accounts:/.test(w);

function split(record, dropped) {
  // The lobbies' writes in order; the rest (presence, sessions) as a multiset.
  const lobby = [], rest = [];
  for (const w of record.writes) (LOBBY_KEYS.test(w.split(" ")[1] ?? "") ? lobby : pong(w, dropped) ? [] : rest).push(w);
  return { lobbyWrites: lobby, otherWrites: rest.sort(), frames: record.frames, state: record.state, delivered: record.delivered, eloratings: record.eloratings };
}

function diff(tsFile, csFile) {
  const ts = JSON.parse(fs.readFileSync(tsFile, "utf8")).steps;
  const cs = JSON.parse(fs.readFileSync(csFile, "utf8")).steps;
  let failed = 0;
  for (const name of Object.keys(STEPS)) {
    if (!ts[name] || !cs[name]) continue;
    const tsN = normalize(ts[name]), csN = normalize(cs[name]);
    if (!seasonless(tsN, (k) => k === "Season:SeasonFive") || !seasonless(csN, (k) => /^Season:\w+$/.test(k))) {
      console.log(`ASSERTION FAILED ${name}: a FullRankUpdate season is not TS's Season:SeasonFive / C#'s current one`);
      failed++;
    }
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

// A reaped game in a match: the server failed the player (MatchStatusEvents), where the same game closing its socket
// left. Each also asserts what the close did, so a step that never reached its condition fails.
const notified = (run, pid, reason) => (run.state[`dll_notifications:${pid}`] ?? []).some((n) => n.includes(`"reason":"${reason}"`));
const noFlag = (run) => !Object.keys(run.state).some((k) => k.startsWith("ranked_disconnect:"));
// A win or a loss recorded (the match's config build writes every player's missing rating document at 1000, so their
// presence says nothing).
const rated = (run) => (run.eloratings ?? []).some((r) => r.wins_1v1 || r.losses_1v1 || r.wins_2v2 || r.losses_2v2);
const CRASH = {
  what: "a reaped game in a match before its result is a crash (match_server_crash, the set dropped, both players told rollback_crash, no rating, no flag) where its close was a leave",
  check: (closed, reaped) => reaped.writes.includes(`set match_server_crash:${MATCH} 1 EX 600`) && noFlag(reaped) && !rated(reaped)
    && notified(reaped, P1, "rollback_crash") && notified(reaped, P2, "rollback_crash")
    && !Object.keys(reaped.state).some((k) => k.startsWith("player_ranked_set:") || /^ranked_set(:|_checkins:|_match:)/.test(k)),
};
const PREGAME_CLOSE = { what: "(the close it is compared with dodged: rated, P1 told opponent_dodge)", check: (closed) => rated(closed) && notified(closed, P1, "opponent_dodge") };
const MIDGAME_CLOSE = { what: "(the close it is compared with flagged the set for P2)", check: (closed) => `ranked_disconnect:${P2}` in closed.state };
const SET_CRASHED = {
  what: "a reaped game between a set's games (or after a game's result) marks the set for its next check-in to drop it unrated (ranked_set_crashed), no flag",
  check: (closed, reaped) => reaped.writes.includes(`set ranked_set_crashed:${SET} gateway_node_gone EX 600`) && reaped.state[`ranked_set_crashed:${SET}`] === "gateway_node_gone"
    && noFlag(reaped) && `ranked_disconnect:${P2}` in closed.state,
};
const REAP_EXPECTED = {
  "match-pregame-dodge": [CRASH, PREGAME_CLOSE],
  "match-mid-game": [CRASH, MIDGAME_CLOSE],
  "match-after-result": [SET_CRASHED],
  "set-between-games": [SET_CRASHED],
};

function diffReap(csFile, reapFile) {
  const cs = JSON.parse(fs.readFileSync(csFile, "utf8")).steps;
  const reaped = JSON.parse(fs.readFileSync(reapFile, "utf8")).steps;
  let failed = 0;
  for (const name of Object.keys(STEPS)) {
    if (!cs[name] || !reaped[name]) continue;
    if (reaped[name].reapedAfterMs == null) {
      console.log(`ASSERTION FAILED ${name}: the player was never reaped`);
      failed++;
      continue;
    }
    // The drop's time inside a published message (a PlayerLeftLobby's JoinedAt, Hydra seconds) is when each run dropped.
    const at = (run) => ({ ...run, writes: run.writes.map((w) => w.replace(/(_hydra_unix_date\\":)\d{10}/g, "$1<s>")) });
    const a = at(normalize(cs[name])), b = at(normalize(reaped[name]));
    seasonless(a, () => true);
    seasonless(b, () => true);
    const expected = REAP_EXPECTED[name];
    for (const e of expected ?? []) {
      let ok;
      try { ok = e.check(a, b); } catch (err) { ok = false; console.log(`  (${err.message})`); }
      console.log(`${ok ? "asserted" : "ASSERTION FAILED"} ${name}: ${e.what}`);
      if (!ok) failed++;
    }
    // A match step's own writes differ by design (asserted above); its lobbies' never do.
    const [x, y] = [split(a, DROPPED[name]), split(b, DROPPED[name])];
    const differences = expected ? compare(x.lobbyWrites, y.lobbyWrites, `${name}.lobbyWrites`, ["close", "reap"]) : compare(x, y, name, ["close", "reap"]);
    for (const d of differences) console.log(`DIFF ${d}`);
    if (differences.length === 0) console.log(`same ${name} (reaped after ${reaped[name].reapedAfterMs} ms)`);
    failed += differences.length;
  }
  process.exit(failed ? 1 : 0);
}

function compare(a, b, path, labels = ["ts", "cs"]) {
  if (JSON.stringify(a) === JSON.stringify(b)) return [];
  if (a && b && typeof a === "object" && typeof b === "object" && !Array.isArray(a) && !Array.isArray(b)) {
    return [...new Set([...Object.keys(a), ...Object.keys(b)])].flatMap((k) => compare(a[k], b[k], `${path}.${k}`, labels));
  }
  return [`${path}:\n   ${labels[0]} ${JSON.stringify(a)}\n   ${labels[1]} ${JSON.stringify(b)}`];
}

const [mode, ...rest] = process.argv.slice(2);
if (mode === "run") await run(rest[0], rest[1], rest[2]);
else if (mode === "diff") diff(rest[0], rest[1]);
else if (mode === "diff-reap") diffReap(rest[0], rest[1]);
else console.log("run ts|cs|reap <out.json> [step] | diff <ts.json> <cs.json> | diff-reap <cs.json> <reap.json>");
