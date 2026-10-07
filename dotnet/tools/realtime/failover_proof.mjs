// The edge's failover proof (slice 3f, docs/REALTIME.md "The edge"): fake games through a real edge to real gateway
// nodes, each its own process, with numbered messages sent to every player all along; then a node is killed, stopped,
// frozen, or two are killed in a row, and every game must still get every message once, in order, without a close or
// a second id frame, and without a ping silence the game would not survive. Run from the repository root (it uses the
// TS server's node_modules), after building the solution (Debug):
//
//   node dotnet/tools/realtime/failover_proof.mjs run <out.json> [scenario]
//
// Scenarios: baseline (no failure: proves the instrument), kill9 (the node holding most games, SIGKILL; then a close
// published while they have no node, and the reaper's time with publishing on), sigterm (that node stopped as an update
// stops it), sigstop (that node frozen: a host that sends nothing; then woken: its stale sockets must release no one),
// twice (three nodes, two SIGKILLs in a row). Scratch Redis and Mongo (REF_REDIS_URL, REF_MONGO_URI: wiped before each
// scenario), the token secret (REF_JWT_SECRET). Ports: nodes 18601.., the edge 18690 (PROOF_PORT moves them); games:
// PROOF_GAMES (30). Each process's output goes to <out.json>.<scenario>.<name>.log.
//
// Shortened on the nodes so a run takes minutes, and said so in the output: the reaper (ReapAfterMs 3 s, every 1 s),
// the detach grace (8 s) and the edge's give-up (6 s). The protections under test (a moved player is never reaped nor let
// go of) must hold whatever these are.
import fs from "node:fs";
import net from "node:net";
import { spawn } from "node:child_process";
import { require, need, openScratch } from "../refdiff/refdiff.mjs";
import { initFrame, decodeFrame } from "../refdiff/gateway.mjs";

const WebSocket = require(process.cwd() + "/node_modules/ws");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { createClient } = require(process.cwd() + "/node_modules/redis");

const GAMES = Number(process.env.PROOF_GAMES ?? 30);
const PORT = Number(process.env.PROOF_PORT ?? 18600);
const EDGE_PORT = PORT + 90;
const INTERVAL = 50;
const SECRET = "failover-proof-edge-secret-0123456789abcdef";
const SHORT = { reapAfterMs: 3000, reapIntervalMs: 1000, graceMs: 8000, giveUpMs: 6000 };
// The game drops a connection it has not been pinged on for about 30 s; the matchmaker drops a queued ticket whose
// heartbeat (refreshed by the answer to the 20 s ping) is 41 s old: ~21 s left after a node's death.
const BUDGET = { pingSilenceMs: 30000, heartbeatMs: 21000 };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const oid = (n) => "000000000000000000f4" + String(n).padStart(4, "0");

// The replay log's script, read from the C# source, so the harness sends messages exactly as the services do.
const LOG_SCRIPT = fs.readFileSync("dotnet/src/OpenVersus.Server.Core/Realtime/PlayerMessages.cs", "utf8").match(/LogScript = """\n([\s\S]*?)\n\s*""";/)[1];
const ID_FRAME = Buffer.concat([Buffer.from([0x09, 0x01, 0x00, 0x24]), Buffer.from("954e7760-539b-436c-a57d-b5623f67a74d", "ascii")]);

// ---- processes ----------------------------------------------------------------------------------------------------

const redisUrl = new URL(need("REF_REDIS_URL"));
const STORES = {
  REDIS: redisUrl.hostname, REDIS_PORT: redisUrl.port || "6379", REDIS_USERNAME: decodeURIComponent(redisUrl.username),
  REDIS_PW: decodeURIComponent(redisUrl.password), Control__Socket: "off", Control__Port: "0", Gateway__EdgeSecret: SECRET,
};
// Only what dotnet needs from this shell: never its REDIS or MONGODB_URI (a sourced dev.env would point a node at the bench).
const BASE_ENV = Object.fromEntries(["PATH", "HOME", "LANG", "TMPDIR", "DOTNET_ROOT", "DOTNET_CLI_HOME"].filter((k) => process.env[k]).map((k) => [k, process.env[k]]));

const procs = [];
function start(name, dll, env, logFile) {
  const out = fs.openSync(logFile, "w");
  // dotnet itself, in its own process group: a signal reaches the service, not a shell in front of it.
  const child = spawn("dotnet", [dll], { env: { ...BASE_ENV, ...STORES, ...env }, detached: true, stdio: ["ignore", out, out] });
  const proc = { name, child, port: Number(env.WEBSOCKET_PORT ?? env.EDGE_PORT), exited: new Promise((r) => child.once("exit", r)) };
  procs.push(proc);
  return proc;
}
const signal = (proc, sig) => { try { process.kill(-proc.child.pid, sig); } catch { /* gone */ } };
async function stopAll() {
  for (const p of procs) signal(p, "SIGCONT");
  for (const p of procs) signal(p, "SIGTERM");
  await Promise.race([Promise.all(procs.map((p) => p.exited)), sleep(8000)]);
  for (const p of procs) signal(p, "SIGKILL");
  procs.length = 0;
}
process.on("exit", () => { for (const p of procs) signal(p, "SIGKILL"); });
// A run stopped from outside (timeout, Ctrl+C) leaves no service holding the ports.
for (const sig of ["SIGTERM", "SIGINT"]) process.on(sig, () => { for (const p of procs) { signal(p, "SIGCONT"); signal(p, "SIGKILL"); } process.exit(130); });

const portFree = (port) => new Promise((resolve) => {
  const s = net.createServer().once("error", () => resolve(false)).once("listening", () => s.close(() => resolve(true))).listen(port, "127.0.0.1");
});

async function until(what, done, ms = 30000) {
  const end = Date.now() + ms;
  while (!(await done())) {
    if (Date.now() > end) throw new Error(`timed out: ${what}`);
    await sleep(100);
  }
}

// The registry's ready ws nodes with their address: { instance: port }.
async function readyNodes(redis) {
  const out = {};
  for (const member of await redis.zRange("ovs:instances", 0, -1)) {
    const [service, instance] = member.split(/\/(.*)/s);
    const report = service === "ws" ? JSON.parse((await redis.get(`ovs:instance:${instance}`)) ?? "null") : null;
    if (report?.state === "Ready" && report.address) out[instance] = Number(new URL(report.address).port);
  }
  return out;
}

// ---- games --------------------------------------------------------------------------------------------------------

class Game {
  constructor(i) {
    this.player = oid(i + 1);
    this.token = jwt.sign({ id: this.player }, need("REF_JWT_SECRET"), { expiresIn: "1h" });
    this.numbers = [];
    this.idFrames = 0;
    this.lastMessageAt = null;
    this.maxSilenceMs = 0;
    this.lastPingAt = null;
    this.maxPingGapMs = 0;
    this.closed = null;
  }

  connect(url) {
    return new Promise((resolve, reject) => {
      const ws = (this.ws = new WebSocket(url, { headers: { "x-forwarded-for": "198.51.100.44" } }));
      const timer = setTimeout(() => reject(new Error(`no id frame for ${this.player}`)), 10000);
      ws.on("open", () => ws.send(initFrame(this.token)));
      ws.on("message", (data) => {
        const bytes = Buffer.from(data), now = Date.now();
        if (bytes.length === 1 && bytes[0] === 0x0c) {
          if (this.lastPingAt) this.maxPingGapMs = Math.max(this.maxPingGapMs, now - this.lastPingAt);
          this.lastPingAt = now;
          ws.send(Buffer.from([0x0a]));
          return;
        }
        if (bytes.equals(ID_FRAME)) {
          this.idFrames++;
          this.lastPingAt ??= now;
          clearTimeout(timer);
          resolve();
          return;
        }
        const message = decodeFrame(bytes);
        if (message?.cmd === "proof") {
          if (this.lastMessageAt) this.maxSilenceMs = Math.max(this.maxSilenceMs, now - this.lastMessageAt);
          this.lastMessageAt = now;
          this.numbers.push(Number(message.n));
        }
      });
      ws.on("close", (code, reason) => { this.closed ??= { code, reason: reason.toString(), at: Date.now() }; });
      ws.on("error", () => {});
    });
  }
}

// ---- the replay log, as the services write it ----------------------------------------------------------------------

async function send(redis, players, json) {
  const payload = `{"playerIds":${JSON.stringify(players)},"message":${json}}`;
  await redis.eval(LOG_SCRIPT, { keys: players.map((p) => `realtime:out:${p}`), arguments: ["ws:send", payload, "message", json, "60000", "300000", ...players] });
}
async function disconnect(redis, player, code, reason) {
  const json = JSON.stringify({ playerId: player, code, reason });
  await redis.eval(LOG_SCRIPT, { keys: [`realtime:out:${player}`], arguments: ["ws:disconnect", json, "disconnect", json, "60000", "300000", player] });
}

// ---- one scenario -------------------------------------------------------------------------------------------------

async function scenario(name, nodeCount, outFile, act) {
  const { redis, close } = await openScratch(`failover_proof_${name}`);
  for (const port of [...Array(nodeCount).keys()].map((i) => PORT + 1 + i).concat(EDGE_PORT)) {
    if (!(await portFree(port))) throw new Error(`port ${port} is taken (PROOF_PORT moves the range)`);
  }
  const nodes = [...Array(nodeCount).keys()].map((i) => start(`node${i + 1}`, "dotnet/src/OpenVersus.Server.Realtime/bin/Debug/net10.0/OpenVersus.Server.Realtime.dll", {
    WEBSOCKET_PORT: String(PORT + 1 + i), WEBSOCKET_ADVERTISE: "127.0.0.1", JWT_SECRET: need("REF_JWT_SECRET"),
    Gateway__ReapAfterMs: String(SHORT.reapAfterMs), Gateway__ReapIntervalMs: String(SHORT.reapIntervalMs), Gateway__EdgeDetachGraceMs: String(SHORT.graceMs),
  }, `${outFile}.${name}.node${i + 1}.log`));
  const result = { scenario: name, short: SHORT, budgets: BUDGET, problems: [] };
  const games = [];
  let publishing = null, running = false, n = 0;
  try {
    await until("every node ready in the registry", async () => Object.keys(await readyNodes(redis)).length === nodeCount);
    start("edge", "dotnet/src/OpenVersus.Server.Edge/bin/Debug/net10.0/OpenVersus.Server.Edge.dll", {
      EDGE_PORT: String(EDGE_PORT), Edge__DrainTimeoutMs: "2000", Edge__GiveUpMs: String(SHORT.giveUpMs),
    }, `${outFile}.${name}.edge.log`);
    await until("the edge listening", async () => !(await portFree(EDGE_PORT)));
    for (let i = 0; i < GAMES; i++) games.push(new Game(i));
    await Promise.all(games.map((g) => g.connect(`ws://127.0.0.1:${EDGE_PORT}`)));
    const players = games.map((g) => g.player);

    // The instrument, before anything else: the first message is in the log as sent, and ws:send carries its seqs.
    const listener = redis.duplicate();
    await listener.connect();
    let sawSeqs = false;
    await listener.subscribe("ws:send", (m) => { sawSeqs ||= m.includes(',"seqs":{'); });
    n = 1;
    await send(redis, players, `{"cmd":"proof","n":1}`);
    await sleep(200);
    const first = (await redis.xRange(`realtime:out:${players[0]}`, "-", "+"))[0];
    if (first?.message?.message !== `{"cmd":"proof","n":1}`) result.problems.push(`instrument: the log holds ${JSON.stringify(first)}`);
    if (!sawSeqs) result.problems.push("instrument: ws:send carried no seqs");
    await listener.quit();

    let paused = false;
    running = true;
    publishing = (async () => {
      while (running) {
        if (!paused) await send(redis, players, `{"cmd":"proof","n":${++n}}`);
        await sleep(INTERVAL);
      }
    })();
    const control = {
      redis, nodes, games, players, result,
      pause: async () => { paused = true; await sleep(INTERVAL * 2); return n; },
      resume: () => { paused = false; },
      // The node holding most games now (realtime:conn names each one's node).
      holder: async () => {
        const counts = {};
        for (const p of players) {
          const node = await redis.hGet(`realtime:conn:${p}`, "node");
          if (node) counts[node] = (counts[node] ?? 0) + 1;
        }
        const [instance, count] = Object.entries(counts).sort((a, b) => b[1] - a[1])[0];
        const port = (await readyNodes(redis))[instance];
        return { instance, count, proc: nodes.find((x) => x.port === port) };
      },
      on: async (player) => redis.hGet(`realtime:conn:${player}`, "node"),
      resumedCount: async () => (await redis.xRange("realtime:connections", "-", "+")).filter((e) => e.message.type === "resumed").length,
      events: async () => (await redis.xRange("realtime:connections", "-", "+")).map((e) => e.message),
      signal,
    };
    await sleep(2000);
    await act(control);
    await sleep(3000);
    running = false;
    await publishing;
    await sleep(1500);

    // Every game: every number once, in order, from 1 to the last (or to its close, for the game a close was sent to),
    // no close otherwise, one id frame, and no silence the game would not survive.
    for (const g of games) {
      const expected = g === result.closeTarget?.game ? result.closeTarget.upTo : n;
      const ok = g.numbers.length === expected && g.numbers.every((x, i) => x === i + 1);
      if (!ok) result.problems.push(`${g.player}: got ${g.numbers.length} numbers (expected 1..${expected})${firstBreak(g.numbers)}`);
      if (g === result.closeTarget?.game) {
        if (g.closed?.code !== result.closeTarget.code) result.problems.push(`${g.player}: closed ${JSON.stringify(g.closed)}, not with ${result.closeTarget.code}`);
      } else if (g.closed) {
        result.problems.push(`${g.player}: closed ${JSON.stringify(g.closed)}`);
      }
      if (g.idFrames !== 1) result.problems.push(`${g.player}: ${g.idFrames} id frames`);
    }
    result.messages = n;
    // The instrument itself: a run that sent next to nothing proves nothing.
    if (n < 20) result.problems.push(`only ${n} messages were sent: the publisher did not run`);
    result.maxSilenceMs = Math.max(...games.map((g) => g.maxSilenceMs));
    result.maxPingGapMs = Math.max(...games.map((g) => g.maxPingGapMs));
    if (result.maxPingGapMs >= BUDGET.pingSilenceMs - 5000) result.problems.push(`a game went ${result.maxPingGapMs} ms without a ping`);
    // No one let go of, but the game a close was sent to (its own close, not reaped).
    const reaped = (await control.events()).filter((e) => e.type === "disconnected" && (e.player !== result.closeTarget?.player || e.reaped));
    if (reaped.length) result.problems.push(`disconnected events: ${reaped.map((e) => `${e.player}${e.reaped ? " (reaped)" : ""}`).join(", ")}`);
    if (result.closeTarget) delete result.closeTarget.game;
  } finally {
    running = false;
    for (const g of games) g.ws?.terminate();
    await stopAll();
    await close();
  }
  result.pass = result.problems.length === 0;
  return result;
}

function firstBreak(numbers) {
  const i = numbers.findIndex((x, k) => x !== k + 1);
  return i < 0 ? "" : `; first break at #${i + 1}: ${numbers.slice(Math.max(0, i - 2), i + 3).join(",")}`;
}

// The node holding most games goes (sig); the time until each of its games is resumed elsewhere.
async function lose(c, sig, label) {
  const holder = await c.holder();
  const resumedBefore = await c.resumedCount();
  const at = Date.now();
  c.signal(holder.proc, sig);
  await until(`${label}: ${holder.count} games resumed elsewhere`, async () => (await c.resumedCount()) - resumedBefore >= holder.count, 60000);
  return { node: holder.proc.name, games: holder.count, resumedAfterMs: Date.now() - at, holder };
}

const SCENARIOS = {
  baseline: { nodes: 2, act: async () => { await sleep(3000); } },
  kill9: {
    nodes: 2,
    act: async (c) => {
      const holder = await c.holder();
      // A close for one of the lost node's games, sent while they have no node: after message k exactly.
      const victims = [];
      for (const g of c.games) if ((await c.on(g.player)) === holder.instance) victims.push(g);
      const resumedBefore = await c.resumedCount();
      const at = Date.now();
      c.signal(holder.proc, "SIGKILL");
      const k = await c.pause();
      await disconnect(c.redis, victims[0].player, 4005, "proof close");
      c.result.closeTarget = { game: victims[0], player: victims[0].player, upTo: k, code: 4005 };
      c.resume();
      await until("the lost node's games resumed", async () => (await c.resumedCount()) - resumedBefore >= victims.length, 60000);
      c.result.lost = { node: holder.proc.name, games: victims.length, resumedAfterMs: Date.now() - at };
      // The reaper's time (the killed node's registry entry: 20 s TTL, then 10 s missing), with publishing on.
      await sleep(36000);
    },
  },
  sigterm: { nodes: 2, act: async (c) => { c.result.lost = await lose(c, "SIGTERM", "SIGTERM"); delete c.result.lost.holder; } },
  sigstop: {
    nodes: 2,
    act: async (c) => {
      const lost = await lose(c, "SIGSTOP", "SIGSTOP");
      c.result.lost = { node: lost.node, games: lost.games, resumedAfterMs: lost.resumedAfterMs };
      // Woken: its stale sockets (the edge let go of them) release no one, even after the grace.
      c.signal(lost.holder.proc, "SIGCONT");
      await sleep(SHORT.graceMs + 3000);
      c.result.wokenNodeReady = Object.values(await readyNodes(c.redis)).includes(lost.holder.proc.port);
      if (!c.result.wokenNodeReady) c.result.problems.push("the woken node is not ready in the registry");
    },
  },
  twice: {
    nodes: 3,
    act: async (c) => {
      const first = await lose(c, "SIGKILL", "first SIGKILL");
      await sleep(1000);
      const second = await lose(c, "SIGKILL", "second SIGKILL");
      c.result.lost = [first, second].map(({ node, games, resumedAfterMs }) => ({ node, games, resumedAfterMs }));
    },
  },
};

const [cmd, outFile, only] = process.argv.slice(2);
if (cmd !== "run" || !outFile) {
  console.error("usage: node dotnet/tools/realtime/failover_proof.mjs run <out.json> [baseline|kill9|sigterm|sigstop|twice]");
  process.exit(2);
}
const results = [];
for (const [name, s] of Object.entries(SCENARIOS)) {
  if (only && only !== name) continue;
  const r = await scenario(name, s.nodes, outFile, s.act);
  results.push(r);
  const lost = [r.lost].flat().filter(Boolean).map((l) => `${l.node}: ${l.games} games resumed after ${l.resumedAfterMs} ms`).join("; ");
  console.log(`${r.pass ? "PASS" : "FAIL"} ${name}: ${r.messages} messages to ${GAMES} games; longest silence ${r.maxSilenceMs} ms (budget ${BUDGET.heartbeatMs} ms), longest without a ping ${r.maxPingGapMs} ms (budget ${BUDGET.pingSilenceMs} ms)${lost ? `; ${lost}` : ""}`);
  for (const p of r.problems.slice(0, 10)) console.log(`  ${p}`);
  if (r.problems.length > 10) console.log(`  ... ${r.problems.length - 10} more`);
}
fs.writeFileSync(outFile, JSON.stringify(results, null, 2));
process.exit(results.every((r) => r.pass) ? 0 : 1);
