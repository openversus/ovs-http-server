// PUT /matches/{id} (the party lobby) on the TS server and the C# port, scenario by scenario: the answer, what each
// member's game was sent (a join: lobby-join, OnLobbyRuntimeDataUpdated, PlayerJoinedLobby), every Redis write and
// publish the server made (Redis MONITOR: TTLs, NX and published payloads included), and the state after.
// Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/lobby_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/lobby_diff.mjs diff <ts.json> <cs.json>
//
// Both servers use the same scratch stores, which `run` wipes before every scenario:
//   REF_REDIS_URL   a throwaway Redis, e.g. redis://default:pw@127.0.0.1:16390
//   REF_MONGO_URI   a scratch database (name containing ref/test/scratch; dropped)
//   REF_JWT_SECRET  the JWT secret both servers use
//   REF_LOBBY_BODY  a captured PUT /matches request body (Hydra), e.g. <corpus>/req/...__matches_ID.bin from
//                   tools/hydra/extract_corpus.py
//   REF_WS_URL      the TS websocket (PR #49's code as committed), in both runs: it turns the TS server's
//                   lobby:player_joined into the three messages and delivers what C# sends through ws:send. P1-P3 stay
//                   connected for the whole run. Each run is checked to use only its own channel.
// Both servers need the client gate on with a minimum of 2026.09.28.1 (MIN_CLIENT_VERSION=2026.09.28.1,
// CLIENT_VERSION_CHECK=true). Never point these at data you want to keep.
import fs from "node:fs";
import { require, need, openScratch, toPlain, openMonitor, writes, state, hydraKeyOrders } from "../refdiff/refdiff.mjs";
import { connectPlayers } from "../refdiff/gateway.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

// The deliberate differences (see PartyLobbyService): for each step, what the difference must be; any other difference
// on that step is reported like any other. (solo-no-session was one until 2026-09-30, when the TS server stopped
// sending NaN for a missing GameplayPreferences: both now send 964, and solo-zero-preferences checks a stored 0 stays 0.)
const EXPECTED = {
  "not-json-lobby": {
    why: "the solo answer, where the TS request fails on JSON.parse and never answers",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.response?.template?.name === "party_lobby"
      && cs.response?.players?.count === 1 && cs.response?.server_data?.LeaderID === P1 && cs.writes.length === 0,
  },
};

function id(n) {
  return `0000000000000000000a${String(n).padStart(4, "0")}`;
}
const P1 = id(1), P2 = id(2), P3 = id(3);
const LOBBY = (n) => id(100 + n);
const CREATED = 1790000000123;

async function run(baseUrl, outFile) {
  const { redis, close } = await openScratch("lobby_diff");
  const body = fs.readFileSync(need("REF_LOBBY_BODY"));
  const token = jwt.sign({ id: P1, profile_id: id(901), wb_network_id: P1, hydraUsername: "OpenVersus_1", username: "PlayerOne" }, need("REF_JWT_SECRET"));
  const games = await connectPlayers(need("REF_WS_URL"), [P1, P2, P3].map((pid, i) => ({
    id: pid, token: i === 0 ? token : jwt.sign({ id: pid, profile_id: id(901 + i), wb_network_id: pid, hydraUsername: `OpenVersus_${i + 1}` }, need("REF_JWT_SECRET")),
  })));

  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  // The harness's own commands (its setup can reach MONITOR after recording starts) are told apart by address.
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];

  const session = (pid, n, fields = {}) => redis.hSet(`connections:${pid}`, {
    id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, wb_network_id: pid, GameplayPreferences: String(440 + n),
    character: `character_c${n}`, skin: `skin_c${n}_default`, clientVersion: "2026.09.28.4", identityRegistered: "1",
    // Steam and Epic ids; a placeholder Steam id and an Epic id; a placeholder and nothing else (see platformIds).
    ...[{ steamId: "76561198000000001", epicId: "epicid0001" }, { steamId: "Unknown", epicId: "epicid0002" }, { steamId: "ip_198.51.100.3" }][n - 1],
    hardwareId: `hw${n}`, installId: `install${n}`, ...fields,
  });
  const loadout = (pid, n) => redis.hSet(`player:${pid}`, { character: `character_l${n}`, skin: `skin_l${n}_default` });
  const lobby = (n, fields) => redis.set(`lobby:${LOBBY(n)}`, JSON.stringify({
    lobbyId: LOBBY(n), ownerId: P2, ownerUsername: "OwnerName", mode: "1v1", playerIds: [P2], createdAt: CREATED, ...fields,
  }), { EX: 3600 });
  const everyone = async () => {
    await session(P1, 1); await session(P2, 2); await session(P3, 3);
    await loadout(P1, 1); await loadout(P2, 2); await loadout(P3, 3);
  };

  const steps = [];
  async function step(name, matchId, setup, { fresh = true } = {}) {
    if (fresh) {
      await redis.flushDb();
      await redis.set("refdiff:scratch", "1");
      await setup?.();
    }
    games.clear();
    recording = [];
    const started = Date.now();
    // A request the server never answers (the TS server, on a lobby that is not JSON) is recorded as such.
    let response, bytes;
    try {
      response = await fetch(`${baseUrl}/matches/${matchId}`, {
        method: "PUT",
        headers: { "content-type": "application/x-ag-binary", "x-hydra-access-token": token },
        body,
        signal: AbortSignal.timeout(8000),
      });
      bytes = Buffer.from(await response.arrayBuffer());
    } catch (e) {
      response = { status: `<no answer: ${e.name}>` };
      bytes = Buffer.alloc(0);
    }
    process.stdout.write(`${name}: ${response.status}\n`);
    // MONITOR lines and the members' messages can arrive just after the answer.
    await new Promise((resolve) => setTimeout(resolve, 600));
    const lines = recording;
    recording = null;
    let decoded;
    try {
      decoded = toPlain(new HydraDecoder(bytes).readValue());
    } catch (e) {
      decoded = `<does not decode: ${e.message}> ${bytes.toString("hex")}`;
    }
    steps.push({
      name, status: response.status, platformIds: fresh ? await platformIds(redis) : steps.at(-1).platformIds, response: normalize(decoded, started), bytes: bytes.length,
      // The wire order of the maps whose keys are integer-like (a decoded answer cannot show it).
      intKeyOrders: (() => { try { return Object.fromEntries(Object.entries(hydraKeyOrders(bytes)).filter(([, lists]) => lists.some((keys) => keys.some((k) => /^\d+$/.test(k))))); } catch (e) { return `<${e.message}>`; } })(),
      // Left out: the TS websocket's presence for the fake games (a pong can land in any step), the C# services' instance
      // registry (their heartbeat lands in whichever step it falls in), and the channel each server
      // carries the members' messages on (recorded apart, compared as the frames).
      frames: Object.fromEntries([P1, P2, P3].map((pid) => [pid, games.frames(pid).filter((f) => !f?.raw).map((f) => normalize(toPlain(f), started))])),
      channels: writes(lines, self).filter((w) => w.startsWith("publish ")).map((w) => w.split(" ")[1]),
      writes: writes(lines, self).filter((w) => !/^publish (ws:send|lobby:player_joined) |^zadd player_heartbeats|^(sadd|srem) online_players|^(zadd|expire) active_ip_accounts:|^(set|zadd|zrem|del) ovs:instance/.test(w)),
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^player_heartbeats$|^online_players$|^active_ip_accounts:|^ovs:instance/.test(k))),
    });
    // The client gate's update toast closes the player's socket 10 s later (the TS websocket's client_update:modal): wait
    // for that, and reconnect, so the next step starts with every game connected on both runs.
    if (writes(lines, self).some((w) => w.startsWith("publish client_update:modal "))) {
      await new Promise((resolve) => setTimeout(resolve, 10600));
      await games.reopen();
    }
  }

  await step("solo-no-lobby", LOBBY(0), everyone);
  await step("solo-own-lobby", LOBBY(1), async () => { await everyone(); await lobby(1, { ownerId: P1, ownerUsername: "PlayerOne", playerIds: [P1] }); });
  await step("arena-own-lobby", LOBBY(11), async () => { await everyone(); await lobby(11, { ownerId: P1, ownerUsername: "PlayerOne", playerIds: [P1], mode: "arena_lobby" }); });
  await step("solo-zero-preferences", LOBBY(1), async () => { await everyone(); await redis.hSet(`connections:${P1}`, "GameplayPreferences", "0"); });
  await step("solo-no-session", LOBBY(0), async () => { await session(P2, 2); });
  await step("custom-lobby", LOBBY(2), async () => { await everyone(); await redis.set(`lobby:${LOBBY(2)}`, JSON.stringify({ Teams: [], LeaderID: P2 })); });
  await step("not-json-lobby", LOBBY(2), async () => { await everyone(); await redis.set(`lobby:${LOBBY(2)}`, "{not json"); });
  await step("fresh-join", LOBBY(6), async () => { await everyone(); await lobby(6, {}); });
  await step("join-2v2-extra-field", LOBBY(6), async () => { await everyone(); await lobby(6, { mode: "2v2", playerIds: [P2, P3], extra: { keep: [1, 2] } }); });
  await step("join-empty-lobby", LOBBY(6), async () => { await everyone(); await lobby(6, { playerIds: [] }); });
  await step("join-already-member", LOBBY(7), async () => { await everyone(); await lobby(7, { playerIds: [P2, P1] }); });
  await step("redirect", LOBBY(3), async () => { await everyone(); await redis.set(`lobby_redirect:${LOBBY(3)}`, LOBBY(4)); await lobby(4, {}); });
  await step("force-joined", LOBBY(9), async () => { await everyone(); await redis.set(`player_lobby:${P1}`, LOBBY(5)); await lobby(5, { playerIds: [P2, P1] }); });
  await step("assigned-own-lobby-ignored", LOBBY(6), async () => { await everyone(); await redis.set(`player_lobby:${P1}`, LOBBY(5)); await lobby(5, { ownerId: P1, playerIds: [P1] }); await lobby(6, {}); });
  await step("join-owner-no-session", LOBBY(6), async () => { await session(P1, 1); await lobby(6, {}); });
  // The owner's session passes the gate but has no names or wb id, and no loadout: the lobby's fallbacks.
  await step("join-owner-sparse", LOBBY(6), async () => { await session(P1, 1); await redis.hSet(`connections:${P2}`, { clientVersion: "2026.09.28.4", identityRegistered: "1" }); await lobby(6, {}); });
  await step("join-blocked-outdated", LOBBY(6), async () => { await everyone(); await redis.hSet(`connections:${P2}`, "clientVersion", "2026.09.01.1"); await lobby(6, {}); });
  await step("join-blocked-again", LOBBY(6), null, { fresh: false });
  await step("join-blocked-no-identity", LOBBY(6), async () => { await everyone(); await redis.hDel(`connections:${P1}`, "identityRegistered"); await lobby(6, {}); });
  await step("refresh-2", LOBBY(8), async () => { await everyone(); await lobby(8, { ownerId: P1, ownerUsername: "PlayerOne", playerIds: [P1, P2] }); });
  // P3 has a session but only a placeholder Steam id: the account id, never the hardware or install id in its session.
  await step("refresh-3", LOBBY(8), async () => { await everyone(); await lobby(8, { ownerId: P1, ownerUsername: "PlayerOne", playerIds: [P1, P2, P3] }); });
  await step("refresh-3-partial", LOBBY(8), async () => { await session(P1, 1); await loadout(P1, 1); await lobby(8, { ownerId: P1, ownerUsername: "PlayerOne", playerIds: [P1, P2, P3] }); });

  monitor.destroy();
  games.close();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

// What the C# port puts in each player's Steam entry (the TS server sends one fixed id for everyone): their Steam id,
// else Epic id, else account id, from their session; "", "Unknown" and "ip_..." are no id. (Read after the request,
// which never writes a session.)
async function platformIds(redis) {
  const real = (id) => typeof id === "string" && id !== "" && id !== "Unknown" && !id.startsWith("ip_");
  const out = {};
  for (const pid of [P1, P2, P3]) {
    const c = await redis.hGetAll(`connections:${pid}`);
    out[pid] = real(c.steamId) ? c.steamId : real(c.epicId) ? c.epicId : pid;
  }
  return out;
}

// What is new on every request, and only that: dates within a minute of the request, fresh ObjectIds (the harness's
// own ids all start 0000), and rand where it is random (it is a constant in the solo answer).
function normalize(value, started) {
  const walk = (v, key) => {
    if (Array.isArray(v)) return v.map((x) => walk(x, key));
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && typeof v._hydra_unix_date === "number" && Math.abs(v._hydra_unix_date * 1000 - started) < 60000) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, walk(x, k)]));
    }
    if (typeof v === "string" && /^[0-9a-f]{24}$/.test(v) && !v.startsWith("0000")) return "<new id>";
    if (key === "rand" && typeof v === "number" && v !== 0.6975513760957894) return "<random>";
    return v;
  };
  return walk(value);
}

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  // The first run is the TS server's: its Steam entries get the ids the port sends instead, in the answer and in every
  // list of members a message carries (players.all, wherever it is).
  const members = (v, out = []) => {
    if (Array.isArray(v)) v.forEach((x) => members(x, out));
    else if (v && typeof v === "object") {
      if (Array.isArray(v.all) && v.all.every((m) => m?.identity)) out.push(...v.all);
      Object.values(v).forEach((x) => members(x, out));
    }
    return out;
  };
  for (const step of a.steps) {
    for (const member of Array.isArray(step.response?.players?.all) ? step.response.players.all : []) {
      const steam = member?.identity?.alternate?.steam?.[0];
      if (steam) {
        // Both are short strings (one length byte), so the answer grows or shrinks by the difference in length.
        const id = step.platformIds[member.account_id];
        step.bytes += Buffer.byteLength(id) - Buffer.byteLength(steam.id);
        steam.id = id;
      }
    }
    for (const member of members(step.frames)) {
      const steam = member.identity.alternate?.steam?.[0];
      if (steam) steam.id = step.platformIds[member.account_id];
    }
  }
  // Each run carries the members' messages on its own channel: TS on lobby:player_joined, C# on ws:send.
  let crossed = 0;
  for (const [run, other] of [[a, "ws:send"], [b, "lobby:player_joined"]]) {
    for (const step of run.steps) if (step.channels.includes(other)) { crossed++; console.log(`${step.name}: ${other} published by ${run === a ? "A" : "B"}`); }
  }
  let differing = 0;
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const x = a.steps[i], y = b.steps[i];
    const parts = ["status", "response", "intKeyOrders", "frames", "writes", "state"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
    if (!parts.length && x?.bytes === y?.bytes) continue;
    if (!parts.length) parts.push("bytes");
    const name = x?.name ?? y?.name;
    if (EXPECTED[name]?.holds(x, y)) {
      console.log(`${name}: differs in ${parts.join(", ")} (expected: ${EXPECTED[name].why})`);
      continue;
    }
    if (EXPECTED[name]) console.log(`${name}: NOT the expected difference (${EXPECTED[name].why})`);
    differing++;
    console.log(`${name}: differs in ${parts.join(", ")}`);
    for (const p of parts) {
      console.log(`  ${p} A: ${JSON.stringify(x?.[p]).slice(0, 1500)}`);
      console.log(`  ${p} B: ${JSON.stringify(y?.[p]).slice(0, 1500)}`);
    }
  }
  differing += crossed;
  console.log(differing ? `${differing} unexpected difference(s)` : "no unexpected differences");
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: lobby_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
