// The client update gate on gameplay transitions (the TS server's requireCurrentClientForGameplay, the C# port's
// ClientGameplayGate) on both servers, scenario by scenario: whether the request is turned away, the answer when it is,
// and every Redis write and publish the server made (Redis MONITOR). Run from the repository root (it uses the TS
// server's node_modules):
//
//   node dotnet/tools/clients/gate_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/clients/gate_diff.mjs diff <ts.json> <cs.json>
//
// Both servers use the same scratch stores, which `run` wipes before every scenario:
//   REF_REDIS_URL   a throwaway Redis, e.g. redis://default:pw@127.0.0.1:16390
//   REF_MONGO_URI   a scratch database (name containing ref/test/scratch; dropped)
//   REF_JWT_SECRET  the JWT secret both servers use
// Both servers need the gate on with a minimum of 2026.09.28.1 (MIN_CLIENT_VERSION=2026.09.28.1,
// CLIENT_VERSION_CHECK=true), and the C# server's Batch:TsUrl must be the TS server. Never point these at data you want
// to keep.
//
// The calendar (get_calendar_events), whose required-update popup follows the same decision, is compared whole: answer
// (its popup's start, now minus a minute, as "<now>"), byte length and writes.
//
// A request the gate lets through reaches the route itself, which the C# port has not ported (a stub, or the TS server
// inside a batch), so for those only the gate's own part is compared: not turned away, and no gate writes. A request
// turned away is compared whole: status, answer, byte length and every write.
//
// The port gates only what leads into a match (queueing, starting a custom match or a rift node, a rematch); lobbies stay
// open. The paths the two servers gate differently are listed in EXPECTED.
import fs from "node:fs";
import { require, need, openScratch, toPlain, openMonitor, writes } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const P1 = "0000000000000000000b0001";
const CURRENT = "2026.09.28.1", NEWER = "2026.10.02.1", OLD = "2026.09.01.1";
const GATED = [
  "/matches/matchmaking/1v1-retail/request", "/matches/matchmaking/ranked-1v1-retail/request", "/matches/matchmaking/2v2-retail/request",
  "/ssc/invoke/create_custom_game_lobby", "/ssc/invoke/join_custom_game_lobby", "/ssc/invoke/start_custom_match",
  "/ssc/invoke/join_party_lobby", "/ssc/invoke/autoparty_join", "/ssc/invoke/set_ready_for_lobby", "/ssc/invoke/rematch_accept",
  "/ssc/invoke/get_or_create_rift_state", "/ssc/invoke/create_rift_lobby", "/ssc/invoke/start_rift_node", "/ssc/invoke/retry_current_rift_node",
];
// set_ready_for_lobby is gated in both (the port's in its party service, for a party lobby: these steps have none).
const LOBBY_ONLY = ["create_custom_game_lobby", "join_custom_game_lobby", "join_party_lobby", "autoparty_join"];
const RIFT_NODES = ["get_or_create_rift_state", "create_rift_lobby", "start_rift_node", "retry_current_rift_node"];

// The deliberate differences: for each step, what the difference must be; any other difference on that step is reported
// like any other.
const EXPECTED = {
  ...Object.fromEntries(LOBBY_ONLY.map((r) => [`gated /ssc/invoke/${r}`, {
    why: "the port leaves lobbies open to a player who must update; the TS server turned the request away",
    holds: (ts, cs) => ts.blocked && !cs.blocked,
  }])),
  ...Object.fromEntries(RIFT_NODES.map((r) => [`gated /ssc/invoke/${r}`, {
    why: "the port gates entering Rifts and a rift node's match; the TS server's gate list had no entry for them",
    holds: (ts, cs) => !ts.blocked && cs.blocked,
  }])),
  "percent-encoded": {
    why: "the C# port gates it; the TS server's gate and its route both match the path still encoded, so neither runs and its catch-all answers",
    // The TS answer is its catch-all's, the one the unrouted suffix-not-gated step gets: the route did not run ungated.
    holds: (ts, cs, tsSteps) => !ts.blocked && ts.writes.length === 0 && cs.blocked
      && JSON.stringify(ts.response) === JSON.stringify(tsSteps.find((s) => s.name === "suffix-not-gated")?.response),
  },
};

// A date within two minutes of the request is "now" (the calendar popup's start is now minus 60 s).
function aroundNow(value, started) {
  if (Array.isArray(value)) return value.map((v) => aroundNow(v, started));
  if (value && typeof value === "object") {
    if (Object.keys(value).length === 1 && typeof value._hydra_unix_date === "number" && Math.abs(value._hydra_unix_date * 1000 - started) < 120000) return "<now>";
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, aroundNow(v, started)]));
  }
  return value;
}

const hydra = (value) => { const e = new HydraEncoder(); e.encodeValue(value); return e.returnValue(); };
const isGateAnswer = (v) => v?.return_code === 1 && v?.body?.error === "client_update_required";
const isGateWrite = (w) => / client_update_modal_|^publish client_update:modal /.test(w);

async function run(baseUrl, outFile) {
  const { redis, close } = await openScratch("gate_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  // The harness's own commands (its setup can reach MONITOR after recording starts) are told apart by address.
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
  const session = (fields) => redis.hSet(`connections:${P1}`, { id: P1, username: "PlayerOne", ...fields });
  const current = () => session({ clientVersion: CURRENT, identityRegistered: "1" });
  const old = () => session({ clientVersion: OLD, identityRegistered: "1" });

  const steps = [];
  async function step(name, { method = "POST", path, body = {}, claims = {}, setup, fresh = true, full = false, noId = false }) {
    if (fresh) {
      await redis.flushDb();
      await redis.set("refdiff:scratch", "1");
      await setup?.();
    }
    const { id: _, ...withoutId } = { id: P1, wb_network_id: P1, username: "PlayerOne", ...claims };
    const token = jwt.sign(noId ? withoutId : { id: P1, wb_network_id: P1, username: "PlayerOne", ...claims }, need("REF_JWT_SECRET"));
    const started = Date.now();
    recording = [];
    let response, bytes;
    try {
      response = await fetch(`${baseUrl}${path}`, {
        method, body: method === "GET" ? undefined : hydra(body), signal: AbortSignal.timeout(8000),
        // Its own address, so the IP step of the account lookup finds nothing it was not given.
        headers: { "content-type": "application/x-ag-binary", "x-hydra-access-token": token, "x-real-ip": "198.51.100.7" },
      });
      bytes = Buffer.from(await response.arrayBuffer());
    } catch (e) {
      response = { status: `<no answer: ${e.name}>` };
      bytes = Buffer.alloc(0);
    }
    await new Promise((resolve) => setTimeout(resolve, 150)); // MONITOR lines can arrive just after the answer
    const lines = recording;
    recording = null;
    let decoded;
    try {
      decoded = bytes.length ? toPlain(new HydraDecoder(bytes).readValue()) : null;
    } catch (e) {
      decoded = `<does not decode: ${e.message}>`;
    }
    const items = path === "/batch" ? decoded?.responses?.map((r) => ({ status: r.status_code, blocked: isGateAnswer(r.body), body: r.body })) : undefined;
    steps.push({ name, full, status: response.status, blocked: isGateAnswer(decoded), response: full ? aroundNow(decoded, started) : decoded, bytes: bytes.length, items, writes: writes(lines, self) });
    process.stdout.write(`${name}: ${response.status}${isGateAnswer(decoded) ? " (turned away)" : ""}\n`);
  }

  const ready = "/ssc/invoke/start_custom_match";
  await step("current-passes", { path: ready, setup: current });
  await step("newer-passes", { path: ready, setup: () => session({ clientVersion: NEWER, identityRegistered: "1" }) });
  await step("old-blocked", { path: ready, setup: old });
  await step("old-blocked-again", { path: ready, fresh: false }); // within the 15 s cooldown: no second toast
  await step("legacy-blocked", { path: ready, setup: () => session({ identityRegistered: "1" }) });
  await step("unregistered-blocked", { path: ready, claims: { identityRegistered: "1" }, setup: () => session({ clientVersion: CURRENT, identityRegistered: "0" }) });
  await step("empty-flag-token-true", { path: ready, claims: { identityRegistered: true }, setup: () => session({ clientVersion: CURRENT, identityRegistered: "" }) });
  await step("empty-flag-token-one", { path: ready, claims: { identityRegistered: "1" }, setup: () => session({ clientVersion: CURRENT, identityRegistered: "" }) });
  await step("empty-version-token-version", { path: ready, claims: { clientVersion: CURRENT }, setup: () => session({ clientVersion: "", identityRegistered: "1" }) });
  await step("no-session-token-claims", { path: ready, claims: { clientVersion: CURRENT, identityRegistered: "1" } });
  await step("no-session-no-claims", { path: ready });
  await step("no-session-old-claims", { path: ready, claims: { clientVersion: OLD, identityRegistered: "1" } });
  for (const path of GATED) await step(`gated ${path}`, { path, setup: old });
  await step("gated GET", { method: "GET", path: "/ssc/invoke/start_custom_match", setup: old });
  await step("gated PUT", { method: "PUT", path: "/ssc/invoke/start_custom_match", setup: old });
  await step("letter-case", { path: "/SSC/Invoke/Start_Custom_Match", setup: old });
  await step("below-path", { path: "/ssc/invoke/start_custom_match/extra", setup: old });
  await step("trailing-slash", { path: "/ssc/invoke/start_custom_match/", setup: old });
  await step("query", { path: "/ssc/invoke/start_custom_match?x=1", setup: old });
  await step("suffix-not-gated", { path: "/ssc/invoke/start_custom_match_x", setup: old });
  await step("percent-encoded", { path: "/ssc/invoke/start%5Fcustom_match", setup: old });
  await step("other-route-not-gated", { method: "GET", path: "/ssc/invoke/get_country_code", setup: old });
  const batch = { options: { allow_failures: true, parallel: true }, requests: [
    { verb: "PUT", url: ready, headers: {}, body: {} },
    { verb: "GET", url: "/ssc/invoke/get_country_code", headers: {} },
  ] };
  // The calendar (get_calendar_events): its required-update popup is there only for a player who must update, with ids
  // from the token's id and the player's toast count. Compared whole; the popup's start is now minus a minute.
  const calendar = { method: "GET", path: "/ssc/invoke/get_calendar_events", full: true };
  const nonce = (value) => async () => { await old(); await redis.set(`client_update_modal_nonce:${P1}`, value); };
  await step("calendar-not-required", { ...calendar, setup: current });
  await step("calendar-required", { ...calendar, setup: old });
  for (const value of ["5", "0x1f", "12abc", "", "abc", "-3"]) await step(`calendar-nonce ${JSON.stringify(value)}`, { ...calendar, setup: nonce(value) });
  await step("calendar-after-toast-toast", { path: ready, setup: old });
  await step("calendar-after-toast", { ...calendar, fresh: false });
  await step("calendar-token-without-id", { ...calendar, noId: true, setup: old });
  await step("calendar-numeric-token-id", { ...calendar, claims: { id: 12345 }, setup: old });
  await step("calendar-no-session", calendar);

  await step("batch-blocked", { method: "PUT", path: "/batch", body: batch, setup: old });
  await step("batch-passes", { method: "PUT", path: "/batch", body: batch, setup: current });

  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")).steps, b = JSON.parse(fs.readFileSync(fileB, "utf8")).steps;
  let problems = 0, blocked = 0;
  const report = (name, what) => { problems++; console.log(`${name}: ${what}`); };
  for (const [i, x] of a.entries()) {
    const y = b[i];
    if (!y || y.name !== x.name) { report(x.name, "step missing"); continue; }
    if (EXPECTED[x.name]) {
      if (EXPECTED[x.name].holds(x, y, a)) console.log(`${x.name}: differs as expected (${EXPECTED[x.name].why}); TS answered ${x.status}`);
      else report(x.name, `not the expected difference (${EXPECTED[x.name].why})`);
      continue;
    }
    if (x.blocked !== y.blocked) { report(x.name, `turned away: ${x.blocked} vs ${y.blocked}`); continue; }
    if (x.full) {
      for (const k of ["status", "bytes"]) if (x[k] !== y[k]) report(x.name, `${k} ${x[k]} vs ${y[k]}`);
      if (JSON.stringify(x.response) !== JSON.stringify(y.response)) report(x.name, "answer differs");
      if (JSON.stringify(x.writes) !== JSON.stringify(y.writes)) report(x.name, "writes differ");
      continue;
    }
    if (x.blocked) {
      blocked++;
      for (const k of ["status", "bytes"]) if (x[k] !== y[k]) report(x.name, `${k} ${x[k]} vs ${y[k]}`);
      if (JSON.stringify(x.response) !== JSON.stringify(y.response)) report(x.name, "answer differs");
      if (JSON.stringify(x.writes) !== JSON.stringify(y.writes)) report(x.name, `writes differ:\n  ${x.writes.join("\n  ")}\n vs\n  ${y.writes.join("\n  ")}`);
    } else if (x.items || y.items) {
      // A batch: every item compared (the C# port sends what it does not turn away to the TS server).
      if (JSON.stringify(x.items) !== JSON.stringify(y.items)) report(x.name, "batch items differ");
      if (x.items?.some((it) => it.blocked)) blocked++;
      const gateA = x.writes.filter(isGateWrite), gateB = y.writes.filter(isGateWrite);
      if (JSON.stringify(gateA) !== JSON.stringify(gateB)) report(x.name, `gate writes differ: ${gateA} vs ${gateB}`);
    } else {
      const gateA = x.writes.filter(isGateWrite), gateB = y.writes.filter(isGateWrite);
      if (gateA.length || gateB.length) report(x.name, `gate writes on a request let through: ${gateA} / ${gateB}`);
    }
  }
  console.log(`${a.length} steps, ${blocked} turned away on both: ${problems ? `${problems} difference(s)` : "no differences"}`);
  process.exit(problems ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run" && args.length === 2) await run(...args);
else if (command === "diff" && args.length === 2) diffRuns(...args);
else {
  console.error("usage: gate_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
