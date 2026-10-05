// The game's websocket on the TS websocket server (src/websocket.ts) and on the C# realtime gateway
// (OpenVersus.Server.Realtime), step by step: every frame each fake game is sent, as raw bytes (the C# websocket Hydra
// encoder is compared byte for byte, never through a decoder), how its socket was closed, and every Redis write the
// server made (MONITOR: commands run by scripts included). Run from the repository root (it uses the TS server's
// node_modules):
//
//   node dotnet/tools/realtime/gateway_diff.mjs run ts <out.json> [step]   the TS websocket (REF_TS_WS)
//   node dotnet/tools/realtime/gateway_diff.mjs run cs <out.json> [step]   the C# gateway (REF_CS_WS)
//   node dotnet/tools/realtime/gateway_diff.mjs diff <ts.json> <cs.json>
//
// Scratch Redis and Mongo, wiped before every step (REF_REDIS_URL, REF_MONGO_URI), and the servers' token secret
// (REF_JWT_SECRET). The TS websocket must be PR #49's code as committed, run from its build as prod runs it. Both servers
// may run at once on the same stores: each step connects to one of them only. The games connect as the reverse proxy
// passes them on (X-Forwarded-For), so the IP-keyed writes name the same address on both.
//
// What the C# gateway writes that TS does not (realtime:conn:{player}, the realtime:connections stream, the ws:disconnect
// it publishes when a connection replaces another) is checked to be there, then left out of the comparison; every other
// difference is either in EXPECTED, which asserts it, or a failure.
import fs from "node:fs";
import { require, need, openScratch, openMonitor } from "../refdiff/refdiff.mjs";
import { initFrame } from "../refdiff/gateway.mjs";
// After gateway.mjs, which loads mvs-dump with the CLI arguments hidden (its modules run a CLI on import otherwise).
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");

const WebSocket = require(process.cwd() + "/node_modules/ws");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const oid = (n) => "00000000000000000027" + String(n).padStart(4, "0");
const [P1, P2, P3] = [1, 2, 3].map(oid);
const IP = "198.51.100.27";
const claims = (pid, n) => ({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP });
const token = (pid, n, options) => jwt.sign(claims(pid, n), need("REF_JWT_SECRET"), options);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const SETTLE = 400;

// What a ws:send carries, as JSON text (so the number spellings below reach both servers as written): the shapes the
// C# services send, and every kind of value the encoder has a case for.
const MESSAGES = {
  update: `{"data":{"template_id":"OnLobbyModeUpdated","LobbyId":"${oid(200)}","ModeString":"2v2"},"payload":{"custom_notification":"realtime"},"header":"","cmd":"update"}`,
  profile: `{"data":{"AccountId":"${P1}","MatchId":"${oid(100)}","template_id":"MatchSetLeaverNotification"},"payload":{"frm":{"id":"internal-server","type":"server-api-key"},"template":"realtime","account_id":"${P1}","profile_id":"${P1}"},"header":"","cmd":"profile-notification"}`,
  numbers: `{"data":{"Int":42,"Neg":-7,"Zero":0,"NegZero":-0,"Byte":255,"Short":65535,"Int32":2147483647,"Big":3000000000,"NegBig":-3000000000,"Safe":9007199254740991,"Half":1.5,"Whole":2.0,"Tiny":0.000001,"NegFloat":-0.25},"cmd":"numbers"}`,
  // One edge value each, so a refusal names its value.
  ...Object.fromEntries(["256", "-129", "-32769", "4294967296", "-2147483649", "9007199254740993", "18446744073709551615", "18446744073709551616", "1e21", "1e300", "-1e21", "5e-324", "1.0e2", "123456789012345678901234567890"]
    .map((v) => [`number ${v}`, `{"data":{"V":${v}},"cmd":"number"}`])),
  values: `{"data":{"True":true,"False":false,"Null":null,"Empty":"","Unicode":"é — 🎮 \\u0000 \\"q\\"","Long":"${"x".repeat(300)}","EmptyArray":[],"EmptyObject":{},"Mixed":[1,"a",null,true,{"k":[]},[2.5]]},"cmd":"values"}`,
  keys: `{"data":{"10":"ten","2":"two","b":"bee","a":"ay","-1":"minus","01":"leading zero","4294967295":"max index","4294967294":"index"},"cmd":"keys"}`,
  dates: `{"data":{"JoinedAt":{"_hydra_unix_date":1700000000}},"cmd":"dates"}`,
  "date as text": `{"data":{"Not":{"_hydra_unix_date":"1700000000"}},"cmd":"date"}`,
  "date with another field": `{"data":{"Two":{"_hydra_unix_date":1,"x":2}},"cmd":"date"}`,
  "date fraction": `{"data":{"D":{"_hydra_unix_date":1700000000.5}},"cmd":"date"}`,
  "date null": `{"data":{"D":{"_hydra_unix_date":null}},"cmd":"date"}`,
  oversized: `{"data":{"Long":"${"y".repeat(70000)}"},"cmd":"oversized"}`,
};

/** A fake game: connects as the proxy passes it on, sends `first` (its first frame), keeps every frame as hex. */
function game(url, first, { answerPings = false } = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url, { headers: { "x-forwarded-for": IP } });
    const g = { ws, frames: [], close: null };
    ws.on("open", () => {
      if (first) ws.send(first);
      resolve(g);
    });
    ws.on("message", (data) => {
      const bytes = Buffer.from(data);
      g.frames.push(bytes.toString("hex"));
      if (answerPings && bytes.length === 1 && bytes[0] === 0x0c) ws.send(Buffer.from([0x0a]));
    });
    ws.on("close", (code, reason) => { g.close = { code, reason: reason.toString() }; });
    ws.on("error", reject);
  });
}

// A session as /access leaves it (the fields the websocket's close reads or deletes).
async function seedSession(redis, pid, n) {
  await redis.hSet(`connections:${pid}`, { ...claims(pid, n), jwt: "the-session-token" });
}

const STEPS = {
  async connect(c) {
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
  },
  async "bad-signature"(c) {
    c.games.P1 = await game(c.url, initFrame(jwt.sign(claims(P1, 1), "not-the-secret-0123456789abcdef0123456789"), 1));
    await sleep(SETTLE);
  },
  async "expired-token"(c) {
    c.games.P1 = await game(c.url, initFrame(jwt.sign({ ...claims(P1, 1), exp: Math.floor(Date.now() / 1000) - 60 }, need("REF_JWT_SECRET")), 1));
    await sleep(SETTLE);
  },
  async "short-frame"(c) {
    c.games.P1 = await game(c.url, Buffer.alloc(8, 1));
    await sleep(SETTLE);
  },
  async "token-length-past-end"(c) {
    const frame = initFrame(token(P1, 1), 1);
    frame.writeUInt16BE(0xffff, 0x13);
    c.games.P1 = await game(c.url, frame);
    await sleep(SETTLE);
  },
  async pong(c) {
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
    c.mark();
    c.games.P1.ws.send(Buffer.from([0x0a]));
    await sleep(SETTLE);
  },
  async send(c) {
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    c.games.P2 = await game(c.url, initFrame(token(P2, 2), 2));
    await sleep(SETTLE);
    c.mark();
    for (const [name, text] of Object.entries(MESSAGES)) {
      // P3 is not connected; P1 is named twice. The marker after each splits P1's frames by message.
      await c.redis.publish("ws:send", `{"playerIds":["${P1}","${P3}","${P1}"],"message":${text}}`);
      await c.redis.publish("ws:send", `{"playerIds":["${P1}","${P2}"],"message":{"cmd":"after ${name}"}}`);
    }
    await c.redis.publish("ws:send", `{"playerIds":[],"message":{"cmd":"to nobody"}}`);
    await c.redis.publish("ws:send", `{"message":{"cmd":"no playerIds"}}`);
    await c.redis.publish("ws:send", `not json`);
    await c.redis.publish("ws:send", `{"playerIds":["${P2}"],"message":{"cmd":"last"}}`);
    await sleep(SETTLE);
  },
  async "forced-disconnect"(c) {
    await seedSession(c.redis, P1, 1);
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
    c.mark();
    c.published = await c.redis.publish("ws:disconnect", JSON.stringify({ playerId: P1 }));
    await sleep(SETTLE);
  },
  async "game-closes"(c) {
    await seedSession(c.redis, P1, 1);
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
    c.mark();
    c.games.P1.ws.close(1000);
    await sleep(SETTLE);
  },
  async replaced(c) {
    c.games.first = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
    c.games.second = await game(c.url, initFrame(token(P1, 1), 2));
    await sleep(SETTLE);
    await c.redis.publish("ws:send", `{"playerIds":["${P1}"],"message":{"cmd":"after the second login"}}`);
    await sleep(SETTLE);
    c.mark();
    // The first game goes away after the second logged in: the player stays online.
    c.games.first.ws.terminate();
    await sleep(SETTLE);
  },
  async "daily-toast-bonus"(c) {
    await c.redis.set(`daily_toast_bonus_pending:${P1}`, "2", { EX: 300 });
    c.games.P1 = await game(c.url, initFrame(token(P1, 1), 1));
    await sleep(SETTLE);
  },
  async "plain-http"(c) {
    const res = await fetch(c.url.replace(/^ws/, "http") + "/anything");
    c.http = { status: res.status, type: res.headers.get("content-type"), body: await res.text() };
  },
};

// P1's frames in the send step, by message: what came before each one's marker (several frames: it was named twice).
function byMessage(frames) {
  // As the TS websocket's send: encodeValue, then returnValue (which adds the websocket framing: 06 and the length).
  const marker = (name) => {
    const encoder = new HydraEncoder(true);
    encoder.encodeValue({ cmd: `after ${name}` });
    return Buffer.from(encoder.returnValue()).toString("hex");
  };
  const out = {};
  let at = frames.indexOf("0c") + 1;
  for (const name of Object.keys(MESSAGES)) {
    const end = frames.indexOf(marker(name), at);
    out[name] = end < 0 ? "marker missing" : frames.slice(at, end);
    at = end + 1;
  }
  return out;
}

// The C#-only keys and channels: proven present by `check`, then dropped from the record compared.
const CS_ONLY = [/^realtime:conn:/, /^realtime:connections$/];

const WRITES = new Set(["set", "setex", "psetex", "expire", "pexpire", "publish", "del", "unlink", "hset", "hmset", "hdel", "sadd", "srem", "zadd", "zrem", "zremrangebyscore", "lpush", "rpush", "lrem", "xadd", "incr"]);
// Every write from any client but the harness; millisecond timestamps (13 digits) become <ms>.
function writesOf(lines, self) {
  return lines
    .filter((line) => line.match(/\[\d+ ([^\]]+)\]/)?.[1] !== self)
    .map((line) => [...line.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]))
    .filter((parts) => parts.length && WRITES.has(parts[0].toLowerCase()) && parts[1] !== "refdiff:scratch" && !/^ovs:instance(s$|:)/.test(parts[1] ?? ""))
    .map((parts) => [parts[0].toLowerCase(), ...parts.slice(1)].join(" ").replace(/\b1\d{12}\b/g, "<ms>"));
}

async function presence(redis) {
  const out = { online_players: (await redis.sMembers("online_players")).sort(), player_heartbeats: await redis.zRange("player_heartbeats", 0, -1) };
  for (const key of (await redis.keys("active_ip_accounts:*")).sort()) out[key] = await redis.zRange(key, 0, -1);
  out.sessions = (await redis.keys("connections:*")).sort();
  out.registry = Object.fromEntries(await Promise.all((await redis.keys("realtime:conn:*")).sort().map(async (k) => [k, await redis.hGet(k, "id") ? "connection" : null])));
  out.events = (await redis.exists("realtime:connections")) ? (await redis.xRange("realtime:connections", "-", "+")).map((e) => e.message.type + " " + e.message.player) : [];
  return out;
}

async function run(side, outFile, only) {
  const url = need(side === "ts" ? "REF_TS_WS" : "REF_CS_WS");
  const steps = {};
  for (const [name, step] of Object.entries(STEPS)) {
    if (only && name !== only) continue;
    const { redis, close } = await openScratch("gateway_diff");
    const lines = [];
    const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => lines.push(line));
    const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
    const c = { url, redis, games: {}, marked: null, mark() { this.marked = lines.length; } };
    let end;
    try {
      await step(c);
    } finally {
      end = lines.length;
      for (const g of Object.values(c.games)) g.ws.terminate();
    }
    await sleep(SETTLE);
    if (name === "send") c.messages = byMessage(c.games.P1.frames);
    steps[name] = {
      ...(c.messages ? { messages: c.messages } : {}),
      games: Object.fromEntries(Object.entries(c.games).map(([k, g]) => [k, { frames: g.frames, close: g.close }])),
      ...(c.http ? { http: c.http } : {}),
      ...(c.published !== undefined ? { heardBy: c.published } : {}),
      // What the server wrote during the step (until the games were dropped), and from the step's mark on.
      writes: writesOf(lines.slice(0, c.marked ?? end), self),
      ...(c.marked !== null ? { writesAfterMark: writesOf(lines.slice(c.marked, end), self) } : {}),
      // Then the games are dropped: what the server does when its sockets go.
      writesAtDrop: writesOf(lines.slice(end), self),
      presence: await presence(redis),
    };
    monitor.destroy();
    await close();
    console.log(`${side} ${name}: ${Object.entries(c.games).map(([k, g]) => `${k} ${g.frames.length} frames, close ${g.close?.code ?? "-"}`).join("; ")}`);
  }
  fs.writeFileSync(outFile, JSON.stringify({ side, ranAt: new Date().toISOString(), steps }, null, 1));
}

// Differences that are the design (docs/../local/SLICE3-GATEWAY.md 3a), each asserted: the step names a check that
// must hold on the records, and the paths it covers are not compared.
const has = (list, w) => (list ?? []).includes(w);
const without = (list, w) => { const i = list?.indexOf(w) ?? -1; if (i >= 0) list.splice(i, 1); };
const REJECTED = {
  what: "a rejected handshake: TS closes with no code (the client sees 1005); .NET cannot send that (it writes 1005 on the wire), C# closes with 1000; nothing is sent before either",
  check: (ts, cs) => ts.games.P1.close.code === 1005 && cs.games.P1.close.code === 1000 && ts.games.P1.frames.length === 0 && cs.games.P1.frames.length === 0,
  adjust: (a, b) => { delete a.games.P1.close; delete b.games.P1.close; },
};
const SESSION_DELETE = {
  what: "the close: TS deletes the session (connections:{player}); in C# that is the disconnect consumer's (slice 3d), the gateway takes the player offline only",
  check: (ts, cs) => has(ts.writesAfterMark, `del connections:${P1}`) && !has(cs.writesAfterMark, `del connections:${P1}`)
    && ts.presence.sessions.length === 0 && cs.presence.sessions.includes(`connections:${P1}`),
  adjust: (a, b) => { without(a.writesAfterMark, `del connections:${P1}`); a.presence.sessions = b.presence.sessions = []; },
};
const EXPECTED = {
  connect: [{
    what: "C# names the connection (realtime:conn:{player}) and appends connected, then disconnected at the drop",
    check: (ts, cs) => cs.writes.some((w) => w.startsWith(`hset realtime:conn:${P1} id `)) && cs.writes.some((w) => w.startsWith("xadd realtime:connections "))
      && cs.writesAtDrop.some((w) => w === `del realtime:conn:${P1}`) && JSON.stringify(cs.presence.events) === JSON.stringify([`connected ${P1}`, `disconnected ${P1}`])
      && !ts.writes.some((w) => w.includes("realtime:")),
    adjust: () => {},
  }],
  "bad-signature": [REJECTED],
  "expired-token": [REJECTED],
  "short-frame": [REJECTED],
  "token-length-past-end": [REJECTED],
  "forced-disconnect": [SESSION_DELETE, {
    what: "the ops command reached the server under test (and, here, the other one too: both subscribe)",
    check: (ts, cs) => ts.heardBy >= 1 && cs.heardBy >= 1,
    adjust: () => {},
  }],
  "game-closes": [SESSION_DELETE],
  replaced: [{
    what: "a second login: TS leaves the first socket open (unpinged, dropped by the harness: 1006); C# closes it (1000 replaced), appends replaced and asks every node to close it (ws:disconnect except the new one)",
    check: (ts, cs) => ts.games.first.close.code === 1006 && cs.games.first.close.code === 1000 && cs.games.first.close.reason === "replaced"
      && cs.writes.some((w) => w.startsWith("publish ws:disconnect ") && w.includes('\\"reason\\":\\"replaced\\"'))
      && cs.presence.events.filter((e) => e === `replaced ${P1}`).length === 1,
    adjust: (a, b) => { delete a.games.first.close; delete b.games.first.close; },
  }],
  send: [
    {
      what: "2^53 + 1: TS sends 2^53 (JSON.parse makes it a double), C# the number as written",
      check: (ts, cs) => ts.messages["number 9007199254740993"][0]?.includes("170020000000000000") && cs.messages["number 9007199254740993"][0]?.includes("170020000000000001"),
      adjust: (a, b) => { delete a.messages["number 9007199254740993"]; delete b.messages["number 9007199254740993"]; },
    },
    {
      what: "2^64 - 1: TS refuses it (as a double it is 2^64), C# sends it (u64 max); 2^64, 1e21 and -1e21 refused by both",
      check: (ts, cs) => ts.messages["number 18446744073709551615"].length === 0 && cs.messages["number 18446744073709551615"][0]?.includes("17ffffffffffffffff")
        && ["number 18446744073709551616", "number 1e21", "number -1e21"].every((m) => ts.messages[m].length === 0 && cs.messages[m].length === 0),
      adjust: (a, b) => { delete a.messages["number 18446744073709551615"]; delete b.messages["number 18446744073709551615"]; },
    },
    {
      what: "a malformed date wrapper (text, null, or beside another key): TS sends something date-shaped, C# refuses it (as its HTTP answers do: HydraCodecTests); no producer writes one",
      check: (ts, cs) => ["date as text", "date null", "date with another field"].every((m) => ts.messages[m].length === 2 && cs.messages[m].length === 0),
      adjust: (a, b) => { for (const m of ["date as text", "date null", "date with another field"]) { delete a.messages[m]; delete b.messages[m]; } },
    },
  ],
  "daily-toast-bonus": [{
    what: "TS sends the daily toast bonus popup at the handshake (OnRewardsGranted) and deletes the flag; in C# that is the connected event's consumer's (slice 3d)",
    check: (ts, cs) => ts.games.P1.frames.length === 3 && cs.games.P1.frames.length === 2 && has(ts.writes, `del daily_toast_bonus_pending:${P1}`),
    adjust: (a, b) => { a.games.P1.frames.pop(); without(a.writes, `del daily_toast_bonus_pending:${P1}`); },
  }],
};

function dropCsOnly(record) {
  const keep = (w) => !CS_ONLY.some((re) => re.test(w.split(" ")[1] ?? "")) && !/^publish ws:disconnect /.test(w);
  const out = structuredClone(record);
  for (const key of ["writes", "writesAfterMark", "writesAtDrop"]) if (out[key]) out[key] = out[key].filter(keep);
  delete out.presence.registry;
  delete out.presence.events;
  delete out.heardBy;
  return out;
}

function diff(tsFile, csFile) {
  const ts = JSON.parse(fs.readFileSync(tsFile, "utf8")).steps;
  const cs = JSON.parse(fs.readFileSync(csFile, "utf8")).steps;
  let failed = 0;
  for (const name of Object.keys(STEPS)) {
    if (!ts[name] || !cs[name]) continue;
    const a = dropCsOnly(ts[name]), b = dropCsOnly(cs[name]);
    const expected = EXPECTED[name] ?? [];
    for (const e of expected) {
      let ok;
      try { ok = e.check(ts[name], cs[name]); } catch (err) { ok = false; console.log(`  (${err.message})`); }
      console.log(`${ok ? "asserted" : "ASSERTION FAILED"} ${name}: ${e.what}`);
      if (!ok) failed++;
      e.adjust(a, b);
    }
    // Pings after the first come on the server's 20 s tick, wherever it falls in a step: only the handshake's is compared.
    for (const r of [a, b]) for (const g of Object.values(r.games)) g.frames = g.frames.filter((f, i) => i < 2 || f !== "0c");
    // The send step's messages are compared one by one (games.P1 is the same frames in a row).
    if (a.messages) { delete a.games.P1; delete b.games.P1; }
    // Writes as multisets: the order of one moment's writes is not compared (the C# close takes the player offline
    // first on purpose); each list as sent is in the records.
    for (const r of [a, b]) for (const key of ["writes", "writesAfterMark", "writesAtDrop"]) if (r[key]) r[key] = [...r[key]].sort();
    const differences = compare(a, b, name);
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
