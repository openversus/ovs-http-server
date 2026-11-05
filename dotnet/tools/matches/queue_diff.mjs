// The matchmaking request and its cancel (POST /matches/matchmaking/{criteria}/request, POST
// /matches/matchmaking/request/{id}/cancel) on the TS server and the C# port, scenario by scenario: the answer, every
// Redis write and publish the server made (MONITOR), what each player's game was sent on the websocket, the state after
// (the queue lists included: the matchmaker removes a ticket by its bytes) and the ratings (eloratings). Run from the
// repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/queue_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/queue_diff.mjs diff <ts.json> <cs.json>
//
// Whether the answer reaches the game before the ticket is queued is not measured here (the websocket's hop is slower
// than either order): OpenVersus.Server.Lobbies.Tests/MatchmakingRequestEndpointTests.
//
// Scratch stores, wiped before every step (never point these at data you want to keep):
//   REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET  as for the other harnesses
//   REF_GAMES_URL  where the games connect: the TS websocket for the TS run (it turns party:queued into the game's
//                  OnMatchmakerStarted and the queue push, and matchmaking:cancel into the cancel), the C# gateway for the
//                  C# run (the lobbies queue and cancel themselves: MatchmakingQueue)
// No matchmaker may run against these stores (it would match the tickets).
//
// The games connect after each step's setup (the gateway keeps presence in Redis), and the queue's writes are compared
// wherever they are made (the TS websocket's included) instead of the party:queued and matchmaking:cancel publishes; the
// 1 s tick is compared as which request each game was ticked for (>= 1 tick in about 2 s), never by count. C#'s own
// realtime:queued is left out. (The mode where the TS websocket also served the C# run went with slice 3e.)
import fs from "node:fs";
import { require, need, openScratch, openMonitor, writes, state } from "../refdiff/refdiff.mjs";
import { connectPlayers } from "../refdiff/gateway.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const oid = (n) => "0000000000000000000c" + String(n).padStart(4, "0");
const P1 = oid(1), P2 = oid(2), P3 = oid(3);
const PLAYERS = [P1, P2, P3];
const LOBBY = oid(100);
const MATCH = oid(700);
const IP = "198.51.100.8";
// Channels whose TS websocket handlers keep state (docs/REALTIME.md): compared as writes, payload included.
const GAMES = process.env.REF_GAMES_URL;
const STATEFUL = new Set(GAMES ? ["match:notifications"] : ["matchmaking:cancel", "party:queued", "match:notifications"]);

// A matchmaking request as the game sends it (MATCH_MAKING_REQUEST).
const REQUEST = {
  data: {
    MultiplayParams: { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682", MultiplayRegionSearchId: 1 },
    crossplay_buckets: ["All", "PC"],
    version: "195303.1.1",
  },
  game_server: { launch_data: { id: 0, profile: "1252922" } },
  match: MATCH,
};
const token = (pid, n) => jwt.sign({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP }, need("REF_JWT_SECRET"));

async function call(baseUrl, path, pid, body) {
  const headers = { "x-hydra-access-token": token(pid, PLAYERS.indexOf(pid) + 1), "x-real-ip": IP };
  let payload;
  if (body !== undefined) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
    headers["content-type"] = "application/x-ag-binary";
  }
  let response, bytes;
  try {
    response = await fetch(`${baseUrl}${path}`, { method: "POST", headers, body: payload, signal: AbortSignal.timeout(6000) });
    bytes = Buffer.from(await response.arrayBuffer());
  } catch (e) {
    return { status: `<no answer: ${e.name}>` };
  }
  let decoded;
  try {
    decoded = new HydraDecoder(bytes).readValue();
  } catch {
    try {
      decoded = JSON.parse(bytes.toString("utf8"));
    } catch {
      decoded = bytes.toString("utf8");
    }
  }
  // The game reads Hydra only: what came over the wire is recorded, not just what it decodes to.
  return { status: response.status, type: response.headers.get("content-type"), bytes: bytes.length > 0 ? "some" : "none", body: decoded };
}

async function run(baseUrl, outFile) {
  need("REF_GAMES_URL");
  const { redis, db, close } = await openScratch("queue_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];

  // The websocket keeps each connection's queued requests (and ticks them every second): every step starts with fresh
  // games, connected before the stores are wiped, the previous ones gone long enough for their disconnect to settle.
  const connect = () => connectPlayers(GAMES ?? need("REF_WS_URL"), PLAYERS.map((id, i) => ({ id, token: token(id, i + 1) })));
  recording = [];
  let games = await connect();
  await sleep(200);
  const wsAddr = recording.find((l) => /"sadd" "online_players"/i.test(l))?.match(/\[\d+ ([^\]]+)\]/)?.[1];
  recording = null;
  if (!wsAddr && !GAMES) throw new Error("could not tell the websocket's Redis connection apart");

  // Every player: a current registered client, a session, a locked loadout with an address and an icon, cosmetics equipped
  // once (player:{id}:cosmetics), a player record; online.
  const everyone = async ({ icon = true } = {}) => {
    for (const [i, pid] of PLAYERS.entries()) {
      const n = i + 1;
      await redis.hSet(`connections:${pid}`, {
        id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, wb_network_id: pid, profile_id: oid(900 + n),
        character: "character_shaggy", skin: "skin_shaggy_default", current_ip: IP, clientVersion: "2026.09.28.4", identityRegistered: "1",
      });
      await redis.hSet(`player:${pid}`, { character: `character_l${n}`, skin: `skin_l${n}_default`, ip: `198.51.100.${10 + n}`, ...(icon ? { profileIcon: `profile_icon_${n}` } : {}) });
      await redis.set(`player:${pid}:cosmetics`, JSON.stringify({ Banner: `banner_${n}` }));
    }
    await db.collection("playertesters").insertMany(PLAYERS.map((pid, i) => ({ _id: new ObjectId(pid), name: `Player${i + 1}`, character: `character_m${i + 1}`, variant: `skin_m${i + 1}_default`, __v: 0 })));
    await redis.sAdd("online_players", PLAYERS);
  };
  const lobby = async (players) => {
    await redis.set(`lobby:${LOBBY}`, JSON.stringify({ lobbyId: LOBBY, ownerId: players[0], ownerUsername: "Player1", mode: players.length > 1 ? "2v2" : "1v1", playerIds: players, createdAt: 1790000000123 }), { EX: 3600 });
    for (const p of players) await redis.set(`player_lobby:${p}`, LOBBY, { EX: 3600 });
  };
  const rating = (pid, fields) => db.collection("eloratings").insertOne({ account_id: pid, username: "", elo_1v1: 1000, elo_2v2: 1000, wins_1v1: 0, losses_1v1: 0, wins_2v2: 0, losses_2v2: 0, ...fields });

  const steps = [];
  // `path` is the route the step calls, or what the step does instead (a function: the answer is what it returns).
  async function step(name, path, pid, body, setup) {
    if (!GAMES && (name === "queued-then-cancelled" || name === "queued-then-dropped")) return;
    games.close();
    await sleep(500);
    if (!GAMES) {
      games = await connect();
      await sleep(300);
    }
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    if (GAMES) {
      games = await connect();
      await sleep(300);
    }
    await setup?.();
    games.clear();
    recording = [];
    const started = Date.now();
    const answer = typeof path === "function" ? await path() : await call(baseUrl, path, pid, body);
    // The ticket is published after the answer; the websocket then sends and pushes.
    await sleep(900);
    const lines = GAMES ? recording : recording.filter((l) => l.match(/\[\d+ ([^\]]+)\]/)?.[1] !== wsAddr);
    recording = null;
    // A C# service's heartbeat into the instance registry lands in any run while one runs on these stores: not the step's.
    // In gateway mode, the websockets' presence for the fake games (a pong can land) and C#'s own realtime:queued too.
    const registry = (w) => /^(set|zadd|zrem|del) ovs:instance/.test(w)
      || (GAMES && /^zadd player_heartbeats|^(sadd|srem) online_players|^(zadd|expire|zremrangebyscore) active_ip_accounts:|^(hset|expire|del) realtime:conn:|^(hset|hdel) realtime:queued /.test(w));
    // A close the C# side schedules (realtime:due) is kept apart: TS closes a toasted game after 10 s (an in-memory timer),
    // the port never does, so in gateway mode none may be scheduled for the update toast.
    const due = (w) => GAMES && w.startsWith("zadd realtime:due ");
    const closes = writes(lines, self).filter(due).map((w) => ({ player: w.match(/playerId\\*":\\*"([0-9a-f]{24})/)?.[1], reason: w.match(/reason\\*":\\*"([a-z-]+)/)?.[1] }));
    const all = writes(lines, self).filter((w) => !registry(w) && !due(w));
    const isTick = (f) => f?.cmd === "matchmaking-tick";
    const frames = Object.fromEntries(Object.entries(games.all()).map(([id, list]) => [id, list.filter((f) => !isTick(f))]));
    let ticked = null;
    if (GAMES) {
      await sleep(1200);
      ticked = Object.fromEntries(Object.entries(games.all()).map(([id, list]) => [id, [...new Set(list.filter(isTick).map((f) => JSON.stringify(f.payload)))].sort()]));
    }
    const ids = new Map();
    steps.push(normalize({
      name, path, status: answer.status, type: answer.type, bytes: answer.bytes, answer: answer.body,
      writes: all.filter((w) => !w.startsWith("publish ") || STATEFUL.has(w.split(" ")[1])),
      published: all.filter((w) => w.startsWith("publish ")).map((w) => w.split(" ")[1]),
      frames,
      ticked,
      closes,
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^active_ip_accounts:|^player_heartbeats$|^ovs:instance|^realtime:conn:|^realtime:connections$|^realtime:queued$|^realtime:due$/.test(k))),
      // The queues byte for byte (state shows a list's type only): the matchmaker removes a ticket by its bytes.
      queues: { "1v1": await redis.lRange("1v1", 0, -1), "2v2": await redis.lRange("2v2", 0, -1) },
      eloratings: await db.collection("eloratings").find({}, { sort: { account_id: 1 } }).toArray().then((d) => JSON.parse(JSON.stringify(d))),
    }, started, ids));
    process.stdout.write(`${name}: ${answer.status}\n`);
  }

  const request = (criteria) => `/matches/matchmaking/${criteria}/request`;
  const cancel = `/matches/matchmaking/request/${oid(800)}/cancel`;
  await step("1v1-solo", request("1v1-retail"), P1, REQUEST, everyone);
  await step("1v1-solo-in-own-lobby", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1]); });
  await step("ranked-1v1", request("ranked-1v1-retail"), P1, REQUEST, everyone);
  await step("1v1-from-party-is-2v2", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); });
  await step("2v2-party", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); });
  await step("2v2-solo", request("2v2-retail"), P1, REQUEST, everyone);
  await step("2v2-teammate-disconnected", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); await redis.del(`connections:${P2}`); });
  await step("2v2-teammate-no-loadout", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); await redis.del(`player:${P2}`); });
  await step("2v2-teammate-no-address", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); await redis.hDel(`connections:${P2}`, "current_ip"); });
  await step("no-loadout", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await redis.del(`player:${P1}`); });
  await step("never-equipped", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await redis.del(`player:${P1}:cosmetics`); });
  await step("no-icon", request("1v1-retail"), P1, REQUEST, () => everyone({ icon: false }));
  await step("no-address", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await redis.hDel(`player:${P1}`, "ip"); });
  await step("no-session", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await redis.del(`connections:${P1}`); });
  await step("no-match-field", request("1v1-retail"), P1, { data: REQUEST.data }, everyone);
  await step("stale-tickets", request("1v1-retail"), P1, REQUEST, async () => {
    await everyone();
    await redis.rPush("1v1", JSON.stringify({ created_at: 1790000000, matchType: "1v1", partyLeaderId: P1, matchmakingRequestId: oid(801), partyId: oid(701), party_size: 1, players: [{ id: P1, region: "MVSI", skill: 0 }] }));
    await redis.rPush("1v1", JSON.stringify({ created_at: 1790000000, matchType: "1v1", partyLeaderId: P3, matchmakingRequestId: oid(803), partyId: oid(703), party_size: 1, players: [{ id: P3, region: "MVSI", skill: 0 }] }));
    await redis.rPush("2v2", JSON.stringify({ created_at: 1790000000, matchType: "2v2", partyLeaderId: P2, matchmakingRequestId: oid(802), partyId: oid(702), party_size: 2, players: [{ id: P2, region: "MVSI", skill: 0 }, { id: P1, region: "MVSI", skill: 0 }] }));
  });
  await step("ranked-set-ended", request("1v1-retail"), P1, REQUEST, async () => {
    await everyone();
    await redis.set(`player_ranked_set:${P1}`, oid(200));
    await redis.set(`player_ranked_set:${P3}`, oid(200));
    await redis.set(`ranked_set:${oid(200)}`, JSON.stringify({ players: [{ playerId: P1 }, { playerId: P3 }] }));
    await redis.set(`ranked_set_checkins:${oid(200)}`, "x");
  });
  await step("outdated-client", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await redis.hSet(`connections:${P1}`, "clientVersion", "2025.01.01.1"); });
  await step("2v2-outdated-teammate", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); await redis.hSet(`connections:${P2}`, "clientVersion", "2025.01.01.1"); });
  await step("rating-new", request("1v1-retail"), P1, REQUEST, everyone);
  await step("rating-for-character", request("1v1-retail"), P1, REQUEST, async () => { await everyone(); await rating(P1, { characters_1v1: { character_l1: { elo: 1234.5 } } }); });
  await step("rating-other-character", request("2v2-retail"), P1, REQUEST, async () => { await everyone(); await lobby([P1, P2]); await rating(P1, { characters_2v2: { character_x: { elo: 1500 } } }); await rating(P2, { characters_2v2: { character_l2: { elo: 900 } } }); });
  await step("cancel-solo", cancel, P1, undefined, everyone);
  await step("cancel-party", cancel, P1, undefined, async () => { await everyone(); await lobby([P1, P2]); await redis.sAdd(`party_ready:${LOBBY}`, [P1, P2]); });
  await step("cancel-no-session", cancel, P1, undefined, async () => { await everyone(); await redis.del(`connections:${P1}`); });
  // Gateway mode only: a queued party cancelled (the other cancel steps hold no ticket, which the cancel ignores).
  await step("queued-then-cancelled", cancel, P1, undefined, async () => {
    await everyone();
    await lobby([P1, P2]);
    await call(baseUrl, request("2v2-retail"), P1, REQUEST);
    await sleep(900);
  });
  // Gateway mode only: a queued party whose second player's game goes away (the TS websocket's close, the C# gateway's
  // disconnect and the lobbies' reader of it).
  await step("queued-then-dropped", async () => { await games.drop([P2]); return { status: "dropped" }; }, P1, undefined, async () => {
    await everyone();
    await lobby([P1, P2]);
    await call(baseUrl, request("2v2-retail"), P1, REQUEST);
    await sleep(900);
  });

  games.close();
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// What is new on every request: times near the request (seconds, ms, Hydra dates, ISO), fresh ids (the harness's own
// all start 0000) renamed in order of first appearance, and UUIDs.
function normalize(step, started, ids) {
  const nearSeconds = (n) => Math.abs(n * 1000 - started) < 60000;
  const nearMs = (n) => Math.abs(n - started) < 60000;
  const uuids = new Map();
  const rename = (s) => s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/g, (u) => (uuids.has(u) || uuids.set(u, `<uuid ${uuids.size + 1}>`), uuids.get(u)))
    .replace(/\b1\d{12}(\.\d+)?\b/g, (n) => (nearMs(Number(n)) ? "<now ms>" : n))
    .replace(/(?<![\d.])1\d{9}(?![\d.])/g, (n) => (nearSeconds(Number(n)) ? "<now s>" : n))
    .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z/g, (d) => (nearMs(Date.parse(d)) ? "<now iso>" : d));
  const walk = (v) => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && typeof v._hydra_unix_date === "number" && nearSeconds(v._hydra_unix_date)) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x)]));
    }
    if (typeof v === "number" && nearMs(v)) return "<now ms>";
    if (typeof v === "number" && Number.isInteger(v) && nearSeconds(v)) return "<now s>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// The deliberate differences (see MatchmakingRequestService): for each step, what the difference must be.
const sorted = (run) => [...(run?.writes ?? [])].sort();
const EXPECTED = {
  "queued-then-cancelled": {
    why: "gateway mode: the same writes; TS's websocket set the players idle after the route had deleted party_ready (a process hop later), C# before",
    holds: (ts, cs) => JSON.stringify(sorted(ts)) === JSON.stringify(sorted(cs))
      && ["status", "type", "bytes", "answer", "frames", "ticked", "state", "queues", "eloratings"].every((p) => JSON.stringify(ts[p]) === JSON.stringify(cs[p]))
      && Object.values(cs.frames).flat().filter((f) => f?.cmd === "matchmaking-cancel").length === 2,
  },
  "queued-then-dropped": {
    why: "gateway mode, a queued party's second game drops: both take the party apart (PlayerLeftLobby to the leader) and the ticket off its list; TS then deletes the dropped player's session keys (C#: a later 3d item) and leaves the leader's game searching, ticked every second, for the ticket it removed; C# cancels it for the leader (matchmaking-cancel with the request's id, set idle) and stops its tick; the dropped player is set idle (TS: in_match, then deleted)",
    holds: (ts, cs) => {
      const mine = (w) => new RegExp(`^del (connections|player):${P2}`).test(w) || w === `hset player:${P2} status in_match`
        || w === `hset player:${P2} status idle` || w === `hset player:${P1} status idle`;
      const cancels = (f) => f?.cmd === "matchmaking-cancel";
      const keep = (state) => Object.fromEntries(Object.entries(state).filter(([k]) => !k.startsWith(`connections:${P2}`) && !k.startsWith(`player:${P2}`) && k !== `player:${P1}`));
      const p1 = (state) => { const { status, ...rest } = state[`player:${P1}`]?.value ?? {}; return [status, JSON.stringify(rest)]; };
      return JSON.stringify(ts.writes.filter((w) => !mine(w))) === JSON.stringify(cs.writes.filter((w) => !mine(w)))
        && ts.writes.includes(`hset player:${P2} status in_match`) && ts.writes.some((w) => w.startsWith(`del connections:${P2}`))
        && cs.writes.includes(`hset player:${P2} status idle`) && cs.writes.includes(`hset player:${P1} status idle`)
        && ts.queues["2v2"].length === 0 && cs.queues["2v2"].length === 0
        && ts.ticked[P1].length === 1 && cs.ticked[P1].length === 0
        && JSON.stringify(ts.frames[P1]) === JSON.stringify(cs.frames[P1].filter((f) => !cancels(f)))
        && cs.frames[P1].filter(cancels).length === 1 && JSON.stringify(cs.frames[P1].find(cancels).payload) === JSON.stringify({ id: ts.ticked[P1][0] && JSON.parse(ts.ticked[P1][0]).id, state: 3 })
        && p1(ts.state)[0] === "queued" && p1(cs.state)[0] === "idle" && p1(ts.state)[1] === p1(cs.state)[1]
        && JSON.stringify(keep(ts.state)) === JSON.stringify(keep(cs.state))
        && JSON.stringify({ ...ts.frames, [P1]: null }) === JSON.stringify({ ...cs.frames, [P1]: null })
        && ["status", "ticked", "eloratings"].every((p) => p === "ticked" ? JSON.stringify({ ...ts.ticked, [P1]: null }) === JSON.stringify({ ...cs.ticked, [P1]: null }) : JSON.stringify(ts[p]) === JSON.stringify(cs[p]));
    },
  },
  "no-icon": {
    why: "a loadout with no profileIcon: the TS server writes undefined into the session, throws and never answers; the port queues the player",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.queues["1v1"].length === 1,
  },
  "2v2-teammate-no-loadout": {
    why: "a teammate with no loadout: the TS server writes undefined into their session, throws and never answers; the port queues the party and leaves that session as it was",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.queues["2v2"].length === 1
      && cs.state[`connections:${P2}`]?.value?.character === "character_shaggy",
  },
};

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const x = a.steps[i], y = b.steps[i];
    // Gateway mode: the C# side (B) schedules no close (TS closed each game it sent the update toast 10 s later; the port
    // keeps an outdated player connected).
    if (x?.ticked && y?.closes?.length) {
      differing++;
      console.log(`${x.name}: closes scheduled (B) ${JSON.stringify(y.closes)}; the port closes no connection here`);
    }
    const parts = ["status", "type", "bytes", "answer", "writes", "frames", "ticked", "state", "queues", "eloratings"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
    if (!parts.length) continue;
    const name = x?.name ?? y?.name;
    if (EXPECTED[name]?.holds(x, y)) {
      console.log(`${name}: differs in ${parts.join(", ")} (expected: ${EXPECTED[name].why})`);
      continue;
    }
    if (EXPECTED[name]) console.log(`${name}: NOT the expected difference (${EXPECTED[name].why})`);
    differing++;
    console.log(`${name}: differs in ${parts.join(", ")}`);
    for (const p of parts) {
      if (p === "writes") {
        const only = (from, other) => from.filter((w, i) => from.slice(0, i).filter((v) => v === w).length >= other.filter((v) => v === w).length);
        console.log(`  writes only A: ${JSON.stringify(only(x?.writes ?? [], y?.writes ?? [])).slice(0, 3000)}`);
        console.log(`  writes only B: ${JSON.stringify(only(y?.writes ?? [], x?.writes ?? [])).slice(0, 3000)}`);
        continue;
      }
      console.log(`  ${p} A: ${String(JSON.stringify(x?.[p])).slice(0, 2500)}`);
      console.log(`  ${p} B: ${String(JSON.stringify(y?.[p])).slice(0, 2500)}`);
    }
  }
  console.log(differing ? `${differing} step(s) differ` : "no differences");
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: queue_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
