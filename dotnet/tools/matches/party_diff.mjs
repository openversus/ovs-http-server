// The party lobby SSC routes on the TS server and the C# port, scenario by scenario: the answer, every Redis write the
// server made (MONITOR), the state after, and what each player's game was sent on the websocket. Run from the
// repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/party_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/matches/party_diff.mjs diff <ts.json> <cs.json>
//
// Scratch stores, wiped before every step (never point these at data you want to keep):
//   REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET  as for the other harnesses
//   REF_WS_URL   the TS websocket running on the same scratch stores: it delivers for both servers (the TS server's
//                channels, the port's ws:send), so the games' frames are compared, not the channels that carry them
//   REF_TS_URL   the TS server on the same stores: custom lobbies are made through it in both runs (the port forwards
//                custom lobby requests to it)
// Publishes are recorded but only those on channels the TS websocket keeps state from are compared as writes; the
// rest are compared by the frames they turn into.
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

const oid = (n) => "0000000000000000000d" + String(n).padStart(4, "0");
const P1 = oid(1), P2 = oid(2), P3 = oid(3);
const LOBBY = (n) => oid(100 + n);
const PLAYERS = [P1, P2, P3];
const IP = "198.51.100.7";
// Channels whose TS websocket handlers keep per-connection state (docs/REALTIME.md): a port publishes these as the TS
// server does until that state moves, so they are compared as writes.
const STATEFUL = new Set(["matchmaking:cancel", "party:queued", "match:notifications", "matchmaking:complete", "perks:notifications", "match:end", "lobby:rejoin"]);

// The fields every party request carries (from the captures).
const COMMON = { AutoPartyPreference: false, CrossplayPreference: 1, GameplayPreferences: 448, HissCrc: 1, LobbyTemplate: "party_lobby", Platform: "PC", Version: "CLIENT:2FAE7-Retail DATA:1 PERKS:1" };
const MULTIPLAY = {
  "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
  "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
  "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
  "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
};
const token = (pid, n) => jwt.sign({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP }, need("REF_JWT_SECRET"));

async function call(baseUrl, route, pid, body) {
  const encoder = new HydraEncoder();
  encoder.encodeValue(body);
  let response, bytes;
  try {
    response = await fetch(`${baseUrl}/ssc/invoke/${route}`, {
      method: "PUT",
      headers: { "content-type": "application/x-ag-binary", "x-hydra-access-token": token(pid, PLAYERS.indexOf(pid) + 1), "x-real-ip": IP },
      body: encoder.returnValue(),
      signal: AbortSignal.timeout(6000),
    });
    bytes = Buffer.from(await response.arrayBuffer());
  } catch (e) {
    return { status: `<no answer: ${e.name}>` };
  }
  let decoded;
  try {
    decoded = new HydraDecoder(bytes).readValue();
  } catch {
    decoded = bytes.toString("utf8");
  }
  return { status: response.status, body: decoded };
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("party_diff");
  const tsUrl = need("REF_TS_URL");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];

  // The websocket's own writes (its handshake, heartbeats and sessions) are not the server's under test: its address is
  // the one that adds the players to online_players while they connect.
  recording = [];
  const games = await connectPlayers(need("REF_WS_URL"), PLAYERS.map((id, i) => ({ id, token: token(id, i + 1) })));
  await sleep(200);
  const wsAddr = recording.find((l) => /"sadd" "online_players"/i.test(l))?.match(/\[\d+ ([^\]]+)\]/)?.[1];
  recording = null;
  if (!wsAddr) throw new Error("could not tell the websocket's Redis connection apart");

  const session = (pid, n, fields = {}) => redis.hSet(`connections:${pid}`, {
    id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, wb_network_id: pid, GameplayPreferences: String(440 + n),
    character: `character_c${n}`, skin: `skin_c${n}_default`, current_ip: IP, clientVersion: "2026.09.28.4", identityRegistered: "1", ...fields,
  });
  const everyone = async ({ online = PLAYERS } = {}) => {
    for (const [i, pid] of PLAYERS.entries()) {
      await session(pid, i + 1);
      await redis.hSet(`player:${pid}`, { character: `character_l${i + 1}`, skin: `skin_l${i + 1}_default` });
    }
    await db.collection("playertesters").insertMany(PLAYERS.map((pid, i) => ({
      _id: new ObjectId(pid), name: `Player${i + 1}`, character: `character_m${i + 1}`, variant: `skin_m${i + 1}_default`,
      profile_icon: i === 2 ? undefined : `profile_icon_${i + 1}`, GameplayPreferences: 440 + i + 1, __v: 0,
    })));
    if (online.length) await redis.sAdd("online_players", online);
  };
  const lobby = (n, fields) => redis.set(`lobby:${LOBBY(n)}`, JSON.stringify({
    lobbyId: LOBBY(n), ownerId: P1, ownerUsername: "Player1", mode: "1v1", playerIds: [P1], createdAt: 1790000000123, ...fields,
  }), { EX: 3600 });
  const inLobby = (pid, n) => redis.set(`player_lobby:${pid}`, LOBBY(n), { EX: 3600 });
  // A custom lobby made by the TS server (the port forwards its requests there), owned by `pid`; its id.
  const customLobby = async (pid) => {
    const made = await call(tsUrl, "create_custom_game_lobby", pid, {});
    const id = made.body?.body?.lobby?.MatchID;
    if (!id) throw new Error(`no custom lobby: ${JSON.stringify(made).slice(0, 300)}`);
    return id;
  };

  const steps = [];
  let custom = null;
  async function step(name, route, pid, body, setup) {
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    custom = null;
    await setup?.();
    games.clear();
    recording = [];
    const started = Date.now();
    const answer = await call(baseUrl, route, pid, typeof body === "function" ? body() : body);
    // The TS server notifies after 200 ms (a loadout lock) or 500 ms (a join), after its answer.
    await sleep(900);
    const lines = recording.filter((l) => l.match(/\[\d+ ([^\]]+)\]/)?.[1] !== wsAddr);
    recording = null;
    const all = writes(lines, self);
    const published = all.filter((w) => w.startsWith("publish "));
    const ids = new Map();
    steps.push(normalize({
      name, route, status: answer.status, answer: answer.body,
      writes: all.filter((w) => !w.startsWith("publish ") || STATEFUL.has(w.split(" ")[1])),
      published: published.map((w) => w.split(" ")[1]),
      frames: games.all(),
      // The websocket's own keys (sessions and heartbeats it refreshes on its 20 s ping) are left out.
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^active_ip_accounts:|^player_heartbeats$/.test(k))),
      playertesters: await db.collection("playertesters").find({}, { sort: { _id: 1 } }).toArray().then((d) => JSON.parse(JSON.stringify(d))),
    }, started, ids));
    process.stdout.write(`${name}: ${answer.status}\n`);
  }

  const create = { ...COMMON, AllMultiplayParams: MULTIPLAY, LobbyType: 0 };
  // create_party_lobby
  await step("create-solo", "create_party_lobby", P1, create, everyone);
  await step("create-rejoin-party", "create_party_lobby", P1, create, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2], mode: "2v2" }); await inLobby(P1, 1); await inLobby(P2, 1); });
  await step("create-rejoin-offline", "create_party_lobby", P1, create, async () => { await everyone({ online: [P1] }); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); });
  await step("create-own-solo-lobby", "create_party_lobby", P1, create, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("create-disabled-character", "create_party_lobby", P3, create, async () => { await everyone(); await db.collection("playertesters").updateOne({ _id: new ObjectId(P3) }, { $set: { character: "character_c022" } }); });
  await step("create-zero-preferences", "create_party_lobby", P1, create, async () => { await everyone(); await redis.hSet(`connections:${P1}`, "GameplayPreferences", "0"); });
  await step("create-fun-fact-no-stats", "create_party_lobby", P1, create, async () => { await everyone(); await redis.set(`fun_fact_pending:${P1}`, "1", { EX: 60 }); });
  // Stats that give exactly one fact, so the pick is not random.
  await step("create-fun-fact", "create_party_lobby", P1, create, async () => {
    await everyone();
    await redis.set(`fun_fact_pending:${P1}`, "1", { EX: 60 });
    await db.collection("playerstats").insertOne({ account_id: P1, aggregate: { totalDamageDodged: 123456 } });
  });
  await step("create-in-custom-lobby", "create_party_lobby", P1, create, async () => { await everyone(); custom = await customLobby(P1); });
  // Sparse players (new ones): a record without variant or icon, no session, no record at all.
  const sparse = async ({ record = "full", session: keep = true } = {}) => {
    await everyone();
    if (record === "bare") await db.collection("playertesters").updateOne({ _id: new ObjectId(P1) }, { $unset: { variant: "", profile_icon: "", character: "" } });
    if (record === "none") await db.collection("playertesters").deleteOne({ _id: new ObjectId(P1) });
    if (!keep) await redis.del(`connections:${P1}`);
  };
  await step("create-bare-record", "create_party_lobby", P1, create, () => sparse({ record: "bare" }));
  await step("create-no-session", "create_party_lobby", P1, create, () => sparse({ session: false }));
  await step("create-no-record", "create_party_lobby", P1, create, () => sparse({ record: "none" }));
  // create_party
  await step("party-new", "create_party", P1, {}, everyone);
  await step("party-existing", "create_party", P1, {}, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  // set_mode_for_lobby
  await step("mode-no-lobby", "set_mode_for_lobby", P1, { ...COMMON, ModeString: "2v2" }, everyone);
  await step("mode-solo", "set_mode_for_lobby", P1, { ...COMMON, ModeString: "evtq_arena" }, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); await redis.hSet(`player:${P1}:lobby:${LOBBY(1)}`, { id: LOBBY(1), created_at: "2026-10-01T00:00:00.000Z", mode: "1v1", owner: P1 }); });
  await step("mode-party", "set_mode_for_lobby", P1, { ...COMMON, ModeString: "2v2" }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); await redis.hSet(`player:${P1}:lobby:${LOBBY(1)}`, { id: LOBBY(1), created_at: "2026-10-01T00:00:00.000Z", mode: "1v1", owner: P1 }); });
  await step("mode-not-owner", "set_mode_for_lobby", P2, { ...COMMON, ModeString: "2v2" }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); });
  // invite_to_player_lobby
  await step("invite", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P2, LobbyId: LOBBY(1), IsSpectator: false }, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("invite-full", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P3, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); });
  await step("invite-member", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P1, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); });
  await step("invite-no-invitee", "invite_to_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); });
  await step("invite-no-lobby-id", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P2 }, everyone);
  await step("invite-custom-lobby", "invite_to_player_lobby", P1, () => ({ ...COMMON, InviteeAccountID: P2, LobbyId: custom, IsSpectator: false }), async () => { await everyone(); custom = await customLobby(P1); });
  // join_party_lobby
  const joinSetup = async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); await redis.set(`pending_join_lobby:${P2}`, LOBBY(1), { EX: 60 }); };
  await step("join-pending", "join_party_lobby", P2, { ...COMMON, LobbyId: LOBBY(9) }, joinSetup);
  await step("join-body-only", "join_party_lobby", P2, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("join-not-found", "join_party_lobby", P2, { ...COMMON, LobbyId: LOBBY(5) }, everyone);
  await step("join-no-lobby-id", "join_party_lobby", P2, { ...COMMON }, everyone);
  await step("join-owner-rejoins", "join_party_lobby", P1, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); });
  await step("join-owner-in-custom", "join_party_lobby", P2, { ...COMMON }, async () => { await joinSetup(); custom = await customLobby(P1); });
  // leave_player_lobby
  await step("leave-party-shared", "leave_player_lobby", P2, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); });
  await step("leave-solo-shared", "leave_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("leave-not-in-lobby", "leave_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(4) }, everyone);
  await step("leave-join-transition", "leave_player_lobby", P2, { ...COMMON, LobbyId: LOBBY(4) }, async () => { await everyone(); await lobby(2, { ownerId: P2, playerIds: [P2] }); await inLobby(P2, 2); await redis.set(`pending_join_lobby:${P2}`, LOBBY(1), { EX: 60 }); });
  await step("leave-genuine-owner", "leave_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(4) }, async () => { await everyone(); await lobby(1, { playerIds: [P1, P2], mode: "2v2" }); await inLobby(P1, 1); await inLobby(P2, 1); });
  await step("leave-genuine-solo", "leave_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(4) }, async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("leave-stale-mapping", "leave_player_lobby", P1, { ...COMMON, LobbyId: LOBBY(4) }, async () => { await everyone(); await inLobby(P1, 1); });
  await step("leave-custom", "leave_player_lobby", P1, () => ({ ...COMMON, LobbyId: custom }), async () => { await everyone(); custom = await customLobby(P1); });
  // set_lobby_joinable / set_lobby_not_joinable / autoparty_join
  await step("joinable", "set_lobby_joinable", P1, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); });
  await step("not-joinable", "set_lobby_not_joinable", P1, { ...COMMON, LobbyId: LOBBY(1) }, async () => { await everyone(); await lobby(1, {}); });
  await step("not-joinable-missing", "set_lobby_not_joinable", P1, { ...COMMON, LobbyId: LOBBY(1) }, everyone);
  await step("autoparty", "autoparty_join", P1, { ...COMMON }, everyone);
  // set_ready_for_lobby
  const ready = (r) => ({ ...COMMON, LobbyId: LOBBY(1), MatchID: LOBBY(1), Ready: r });
  await step("ready-solo", "set_ready_for_lobby", P1, ready(true), async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("ready-first-of-two", "set_ready_for_lobby", P1, ready(true), async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); });
  await step("ready-second-of-two", "set_ready_for_lobby", P2, ready(true), async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await redis.sAdd(`party_ready:${LOBBY(1)}`, P1); });
  await step("unready", "set_ready_for_lobby", P2, ready(false), async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await redis.sAdd(`party_ready:${LOBBY(1)}`, [P1, P2]); });
  await step("ready-no-lobby", "set_ready_for_lobby", P1, ready(true), everyone);
  await step("ready-custom", "set_ready_for_lobby", P1, () => ({ ...COMMON, LobbyId: custom, MatchID: custom, Ready: true }), async () => { await everyone(); custom = await customLobby(P1); });
  // lock_lobby_loadout
  const lock = (character, skin = "skin_x") => ({ ...COMMON, Loadout: { Character: character, Skin: skin }, LobbyId: LOBBY(1) });
  await step("lock-solo", "lock_lobby_loadout", P1, lock("character_shaggy", "c002_s14"), async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("lock-party", "lock_lobby_loadout", P1, lock("character_batman", "skin_batman_default"), async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); });
  await step("lock-party-cached-cosmetics", "lock_lobby_loadout", P2, lock("character_taz", "skin_taz_default"), async () => { await everyone(); await lobby(1, { playerIds: [P1, P2] }); await inLobby(P1, 1); await inLobby(P2, 1); await redis.hSet(`connections:${P2}:cosmetics`, { Banner: JSON.stringify("banner_x") }); });
  await step("lock-disabled-character", "lock_lobby_loadout", P1, lock("character_C022"), async () => { await everyone(); await lobby(1, {}); await inLobby(P1, 1); });
  await step("lock-no-player-record", "lock_lobby_loadout", P1, lock("character_shaggy"), async () => { await everyone(); await db.collection("playertesters").deleteOne({ _id: new ObjectId(P1) }); });
  await step("lock-no-session", "lock_lobby_loadout", P1, lock("character_shaggy"), () => sparse({ session: false }));
  await step("lock-bare-record", "lock_lobby_loadout", P1, lock("character_shaggy"), () => sparse({ record: "bare" }));
  await step("lock-custom", "lock_lobby_loadout", P1, () => ({ ...lock("character_batman"), LobbyId: custom }), async () => { await everyone(); custom = await customLobby(P1); });

  games.close();
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// What is new on every request, and only that: times within a minute of the request (seconds in Hydra dates, ms in
// stored JSON), fresh ids (the harness's own all start 0000) renamed in order of first appearance, so a step still shows
// which new ids are the same, and Math.random.
function normalize(step, started, ids) {
  const nearSeconds = (n) => Math.abs(n * 1000 - started) < 60000;
  const nearMs = (n) => Math.abs(n - started) < 60000;
  const rename = (s) => s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
    .replace(/\b1\d{12}\b/g, (n) => (nearMs(Number(n)) ? "<now ms>" : n))
    .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z/g, (d) => (nearMs(Date.parse(d)) ? "<now iso>" : d));
  const walk = (v, key) => {
    if (Array.isArray(v)) return v.map((x) => walk(x, key));
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && typeof v._hydra_unix_date === "number" && nearSeconds(v._hydra_unix_date)) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x, k)]));
    }
    if (key === "rand" && typeof v === "number") return "<random>";
    if (typeof v === "number" && nearMs(v)) return "<now ms>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// The deliberate differences (see PartyService): for each step, what the difference must be; any other difference on
// that step is reported like any other.
const EXPECTED = {
  "create-no-session": {
    why: "a player with no session: the port puts their own cosmetics in the match copy; the TS server wrote those of \"undefined\"",
    holds: (ts, cs) => ts.writes.some((w) => w.startsWith("set player:undefined:cosmetics")) && !cs.writes.some((w) => w.includes("undefined"))
      && JSON.stringify(ts.answer) === JSON.stringify(cs.answer),
  },
  "lock-no-session": {
    why: "a player with no session: the port locks the loadout of the account the token names; the TS server never answers",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.answer?.body?.bAreAllLoadoutsLocked === true,
  },
  "lock-disabled-character": {
    why: "the port answers a refused lock (bAreAllLoadoutsLocked false); the TS server never answers",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.answer?.body?.bAreAllLoadoutsLocked === false,
  },
  "lock-no-player-record": {
    why: "the port answers a refused lock (bAreAllLoadoutsLocked false); the TS server never answers",
    holds: (ts, cs) => String(ts.status).startsWith("<no answer") && cs.status === 200 && cs.answer?.body?.bAreAllLoadoutsLocked === false,
  },
};

// The port writes lobby_id into a session (connections:{id}) as one field; the TS server writes the whole session
// back. The state after is compared; the writes are left out of the comparison.
const sessionWrite = (w) => /^hset connections:[^: ]+ /.test(w);
// The fun fact flag is taken with GETDEL by the port, GET then DEL by the TS server: the state after shows it went.
const flagWrite = (w) => /^del fun_fact_pending:/.test(w);

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  for (const step of [...a.steps, ...b.steps]) step.writes = step.writes?.filter((w) => !sessionWrite(w) && !flagWrite(w));
  let differing = 0;
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const x = a.steps[i], y = b.steps[i];
    const parts = ["status", "answer", "writes", "frames", "state", "playertesters"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
    if (!parts.length) continue;
    const name = x?.name ?? y?.name;
    if (EXPECTED[name]?.holds(x, y)) {
      console.log(`${name}: differs in ${parts.join(", ")} (expected: ${EXPECTED[name].why})`);
      continue;
    }
    if (EXPECTED[name]) console.log(`${name}: NOT the expected difference (${EXPECTED[name].why})`);
    differing++;
    console.log(`${x?.name ?? y?.name}: differs in ${parts.join(", ")}`);
    for (const p of parts) {
      if (p === "writes") {
        // As two lists: what only one side wrote.
        const only = (from, other) => from.filter((w, i) => from.slice(0, i).filter((v) => v === w).length >= other.filter((v) => v === w).length);
        console.log(`  writes only A: ${JSON.stringify(only(x?.writes ?? [], y?.writes ?? [])).slice(0, 3000)}`);
        console.log(`  writes only B: ${JSON.stringify(only(y?.writes ?? [], x?.writes ?? [])).slice(0, 3000)}`);
        continue;
      }
      console.log(`  ${p} A: ${String(JSON.stringify(x?.[p])).slice(0, 2000)}`);
      console.log(`  ${p} B: ${String(JSON.stringify(y?.[p])).slice(0, 2000)}`);
    }
  }
  console.log(differing ? `${differing} step(s) differ` : "no differences");
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: party_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}
