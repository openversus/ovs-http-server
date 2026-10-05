// A match's end on the TS websocket (handleOnMatchEnd, from match:end) and on the C# match flow (MatchEnd, from
// /ovs_end_match with MatchEnd:Enabled), scenario by scenario: what each player's game is sent (EndOfMatchPayload,
// FullRankUpdate, MatchSetLeaverNotification, the empty config, RematchDeclinedNotification, in order, after the delays),
// every Redis write the server made (MONITOR), the Redis state after, and the ratings and set stats in Mongo. Run from the
// repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/match_end_diff.mjs run ts <out.json>   the TS websocket (REF_WS_URL) ends the match
//   node dotnet/tools/matches/match_end_diff.mjs run cs <out.json>   the C# match flow (REF_CS_URL) ends it
//   node dotnet/tools/matches/match_end_diff.mjs diff <ts.json> <cs.json>
//
// The TS websocket runs in both: in the C# run it only delivers what C# sends through ws:send (C# publishes no
// match:end there, so the TS websocket never ends the match). Each step publishes the match's notification first, so the
// TS websocket holds each player's config and the C# match flow keeps it (GameplayConfigs:Mode Shadow: the TS websocket
// is the one that writes beside the config, in both runs, so nothing races at the config), then seeds the set
// as RankedSets and submit_end_of_match_stats leave it, then ends the match. game_result_received is seeded for every
// match that finished, so the TS websocket's own disconnect handling (when a step closes its games) is a normal leave.
// The TS websocket must be PR #49's code as committed. Scratch stores, wiped before every step: REF_REDIS_URL,
// REF_MONGO_URI, REF_JWT_SECRET, as the other harnesses.
import fs from "node:fs";
import { require, need, openScratch, openMonitor, writes, state } from "../refdiff/refdiff.mjs";
import { connectPlayers } from "../refdiff/gateway.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { EJSON } = require(process.cwd() + "/node_modules/bson");

const oid = (n) => "00000000000000000019" + String(n).padStart(4, "0");
const [P1, P2, P3, P4] = [1, 2, 3, 4].map(oid);
const BOT = oid(11);
const HUMANS = [P1, P2, P3, P4];
const MATCH = oid(100), SET = oid(101), LOBBY = oid(200);
const KEY = "the-match-key";
const IP = "198.51.100.8";
const token = (pid, n) => jwt.sign({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP }, need("REF_JWT_SECRET"));

const human = (playerId, playerIndex, teamIndex) => ({ playerId, partyId: MATCH, playerIndex, teamIndex, isHost: playerIndex === 0, ip: IP, isBot: false });
const bot = (playerId, playerIndex, teamIndex) => ({ playerId, partyId: MATCH, playerIndex, teamIndex, isHost: false, ip: IP, isBot: true });
const notification = (players, extra = {}) => ({ players, matchId: MATCH, matchKey: KEY, map: "M001_V2", mode: "1v1", rollbackPort: 57003, p2p: false, ...extra });
const ONE_V_ONE = () => [human(P1, 0, 0), human(P2, 1, 1)];
const TWO_V_TWO = () => [human(P1, 0, 0), human(P2, 1, 1), human(P3, 2, 0), human(P4, 3, 1)];

async function run(side, outFile) {
  if (side !== "ts" && side !== "cs") throw new Error("run ts|cs <out.json>");
  const { redis, db, close } = await openScratch("match_end_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
  const connect = () => connectPlayers(need("REF_WS_URL"), HUMANS.map((id, i) => ({ id, token: token(id, i + 1) })));
  let games = await connect();

  const session = (pid, fields = {}) => redis.hSet(`connections:${pid}`, { id: pid, username: `U-${pid.slice(-2)}`, current_ip: IP, character: "character_jake", skin: "skin_jake_default", ...fields });
  const rating = (pid, fields) => db.collection("eloratings").insertOne({ account_id: pid, username: `U-${pid.slice(-2)}`, elo_1v1: 1000, elo_2v2: 1000, wins_1v1: 0, losses_1v1: 0, wins_2v2: 0, losses_2v2: 0, ...fields });
  // A set as the matchmaker made it (game 1: the set's id is the match's) and submit_end_of_match_stats counted the
  // game's winner into it (RecordWinnerAsync).
  const set = async ({ id = SET, game = MATCH, players = ONE_V_ONE(), mode = "1v1", gamesPlayed = 0, scores = [1, 0] } = {}) => {
    await redis.set(`ranked_set:${id}`, JSON.stringify({ players, mode, gamesPlayed, scores, checkins: [] }), { EX: 1200 });
    for (const p of players) await redis.set(`player_ranked_set:${p.playerId}`, id, { EX: 1200 });
    if (game !== id) {
      await redis.set(`ranked_set_match:${id}`, game, { EX: 1800 });
      await redis.set(`match_to_set:${game}`, id, { EX: 1200 });
    }
  };

  const steps = [];
  async function step(name, { config, seed, ended = true, twice = false }) {
    games.close();
    await sleep(400);
    games = await connect();
    await sleep(300);
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    // Every fake game is connected: online, as the websocket keeps them.
    await redis.sAdd("online_players", HUMANS);
    for (const p of config.players.filter((p) => !p.isBot)) {
      await session(p.playerId);
      await redis.hSet(`connections:${p.playerId}:cosmetics`, { Banner: JSON.stringify("banner_one"), Taunts: JSON.stringify({}) });
    }
    await seed?.();
    // The match as launched: its config kept and published (the TS websocket holds it per connection, C# per player).
    await redis.set(MATCH, JSON.stringify(config), { EX: 1200 });
    await redis.publish("match:notifications", JSON.stringify(config));
    await sleep(1500);
    if (ended) await redis.set(`game_result_received:${MATCH}`, "1", { EX: 600 });
    games.clear();
    recording = [];
    const started = Date.now();
    for (let i = 0; i < (twice ? 2 : 1); i++) {
      if (side === "ts") {
        // What /ovs_end_match publishes (the config's players, bots and spectators included, in order).
        await redis.publish("match:end", JSON.stringify({ playersIds: config.players.map((p) => p.playerId), matchId: MATCH }));
      } else {
        await fetch(`${need("REF_CS_URL")}/ovs_end_match`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ matchId: MATCH, key: KEY }) });
      }
      await sleep(400);
    }
    await sleep(3000);
    const lines = recording;
    recording = null;
    // Left out: each server's own bookkeeping (the TS websocket's presence for the fake games, the C# instance registry),
    // the messages C# publishes for the TS websocket to deliver (compared as frames), and C#'s own keys, kept apart.
    const all = writes(lines, self)
      .filter((w) => !/^(set|zadd|zrem|del) ovs:instance|^zadd player_heartbeats|^(sadd|srem) online_players|^(zadd|expire|zremrangebyscore) active_ip_accounts:|^publish ws:send /.test(w))
      .map((w) => w.replace(/\\(["\\])/g, "$1"))
      // DEL of several keys, as one each (the C# client sends them together).
      .flatMap((w) => (w.startsWith("del ") ? w.split(" ").slice(1).map((k) => `del ${k}`) : [w]));
    const own = (w) => /^(zadd|zrem) realtime:due|^set match_end:|^del match_config:|^set rejoin_pending:|^del ranked_set_match:|^set ranked_set_lock:|^del ranked_set_lock:/.test(w);
    const frames = Object.fromEntries(HUMANS.map((id) => [id, games.frames(id).filter((f) => !f?.raw).map((f) => normalize(f, started))]));
    const mongo = {};
    for (const c of ["eloratings", "playerstats"]) {
      const docs = await db.collection(c).find({}, { promoteValues: false, sort: { account_id: 1 } }).toArray();
      mongo[c] = JSON.parse(EJSON.stringify(docs.map(({ _id, ...rest }) => rest), { relaxed: false }));
      for (const d of mongo[c]) for (const k of ["updated_at", "last_updated", "updatedAt"]) if (d[k]) d[k] = "<set>";
    }
    steps.push({
      name,
      frames,
      writes: all.filter((w) => !own(w)).sort(),
      own: [...new Set(all.filter(own).map((w) => w.split(" ").slice(0, 2).join(" ")))].sort(),
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^ovs:instance|^player_heartbeats$|^online_players$|^active_ip_accounts:|^match:results$|^realtime:due$|^match_end:|^match_config:|^rejoin_pending:/.test(k))),
      mongo,
    });
    console.log(`${name}: ${Object.entries(frames).map(([id, f]) => `${id.slice(-2)}:${f.map((x) => x?.data?.template_id ?? "?").join("+") || "-"}`).join(" ")}`);
  }

  await step("set-game1-continues", { config: notification(ONE_V_ONE()), seed: () => set({ id: MATCH, scores: [1, 0] }) });
  await step("set-game1-pending-winner", { config: notification(ONE_V_ONE()), seed: () => redis.set(`ranked_set_pending_winner:${MATCH}`, "1", { EX: 120 }) });
  await step("set-game2-continues", { config: notification(ONE_V_ONE()), seed: () => set({ game: MATCH, gamesPlayed: 1, scores: [1, 1] }) });
  await step("set-over-2-0", {
    config: notification(ONE_V_ONE()),
    seed: async () => {
      await set({ game: MATCH, gamesPlayed: 1, scores: [2, 0] });
      await redis.set(`match_characters:${MATCH}`, JSON.stringify({ [P1]: "character_jake", [P2]: "character_finn" }), { EX: 1200 });
      await rating(P1, { elo_1v1: 1100, wins_1v1: 3, losses_1v1: 1, characters_1v1: { character_jake: { elo: 1100, wins: 3, losses: 1, streak: 1 } } });
      await db.collection("playerstats").insertOne({ account_id: P1, characters_1v1: { character_jake: { totalDamageDealt: 1234.5, ringouts: 7 } } });
    },
  });
  await step("set-over-game3-2-1", {
    config: notification(ONE_V_ONE()),
    seed: async () => {
      await set({ game: MATCH, gamesPlayed: 2, scores: [1, 2] });
      await redis.set(`match_characters:${MATCH}`, JSON.stringify({ [P1]: "character_jake", [P2]: "character_finn" }), { EX: 1200 });
    },
  });
  await step("set-over-no-fighter", {
    // P2 has no fighter anywhere: rated without one, so FullRankUpdate's per-fighter data is empty (BestCharacter -1).
    config: notification(ONE_V_ONE()),
    seed: async () => {
      await set({ game: MATCH, gamesPlayed: 1, scores: [0, 2] });
      await redis.set(`match_characters:${MATCH}`, JSON.stringify({ [P1]: "character_jake" }), { EX: 1200 });
      await redis.hDel(`connections:${P2}`, "character");
    },
  });
  await step("set-over-2v2", {
    config: notification(TWO_V_TWO(), { mode: "2v2", map: "M001" }),
    seed: async () => {
      await set({ game: MATCH, players: TWO_V_TWO(), mode: "2v2", gamesPlayed: 1, scores: [0, 2] });
      await redis.set(`match_characters:${MATCH}`, JSON.stringify({ [P1]: "character_jake", [P2]: "character_finn", [P3]: "character_arya", [P4]: "character_C017" }), { EX: 1200 });
    },
  });
  await step("stale-set-pointer", { config: notification(ONE_V_ONE()), seed: () => redis.set(`player_ranked_set:${P1}`, SET, { EX: 600 }) });
  await step("orphan", { config: notification(ONE_V_ONE()) });
  await step("set-resolved", {
    config: notification(ONE_V_ONE()),
    seed: async () => { await set({ game: MATCH, gamesPlayed: 1, scores: [1, 1] }); await redis.set(`elo_processed_set:${SET}`, "concede", { EX: 300 }); },
  });
  await step("crash", {
    config: notification(ONE_V_ONE()),
    ended: false,
    seed: async () => { await set({ game: MATCH, gamesPlayed: 1, scores: [1, 0] }); await redis.set(`match_server_crash:${MATCH}`, "1", { EX: 600 }); await redis.set(`ranked_disconnect:${P2}`, SET, { EX: 600 }); },
  });
  await step("dodge-flag-names-the-set", {
    config: notification(ONE_V_ONE()),
    seed: async () => { await set({ game: MATCH, gamesPlayed: 0, scores: [1, 0] }); await redis.set(`ranked_disconnect:${P2}`, SET, { EX: 600 }); },
  });
  await step("dodge-flag-from-elsewhere", {
    config: notification(ONE_V_ONE()),
    seed: async () => { await set({ game: MATCH, gamesPlayed: 0, scores: [1, 0] }); await redis.set(`ranked_disconnect:${P2}`, "1", { EX: 600 }); },
  });
  await step("casual-decline", {
    config: notification([human(P1, 0, 0), bot(BOT, 1, 1)], { isCustomGame: true, gameplayConfigOverride: { bIsCustomGame: false } }),
    seed: () => redis.set(`match:${MATCH}`, JSON.stringify({ matchId: MATCH, tickets: [], isPasswordMatch: true }), { EX: 1200 }),
  });
  await step("rift-no-set", {
    config: notification([human(P1, 0, 0), bot(BOT, 1, 1)], { gameplayConfigOverride: { bIsRift: true, bIsRanked: false } }),
    seed: async () => {
      await redis.set(`match:${MATCH}`, JSON.stringify({ matchId: MATCH, tickets: [], isPasswordMatch: true }), { EX: 1200 });
      // RecordWinnerAsync's fallback for a game with no set: TS took it as game 1 of a set.
      await redis.set(`ranked_set_pending_winner:${MATCH}`, "0", { EX: 120 });
    },
  });
  // Last: the TS websocket's 45 s party timer outlives the step.
  await step("party-kept", {
    config: notification([human(P1, 0, 0), bot(BOT, 1, 1)], { isCustomGame: true }),
    seed: async () => {
      await redis.set(`lobby:${LOBBY}`, JSON.stringify({ lobbyId: LOBBY, ownerId: P1, ownerUsername: "U-01", mode: "2v2", playerIds: [P1, P3], createdAt: 1790000000123 }), { EX: 3600 });
      await redis.set(`player_lobby:${P1}`, LOBBY, { EX: 3600 });
      await redis.sAdd(`party_ready:${LOBBY}`, [P1, P3]);
    },
  });

  games.close();
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ side, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

// The time a message was made (Created, LastUpdateTimestamp): near the step, then set aside.
function normalize(frame, started) {
  const walk = (v) => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && "_hydra_unix_date" in v && Math.abs(v._hydra_unix_date - started / 1000) < 120) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, walk(x)]));
    }
    if (typeof v === "number" && Math.abs(v - started / 1000) < 120) return "<now>";
    return v;
  };
  return walk(frame);
}

const clone = (v) => JSON.parse(JSON.stringify(v));
const SEASON_TS = "Season:SeasonFive";

// FullRankUpdate is for Season:Current in C# (default (d), step 4 plan); TS always SeasonFive. Asserted, then aligned.
function seasons(run, current) {
  let renamed = 0;
  for (const list of Object.values(run.frames)) {
    for (const f of list) {
      const data = f?.data?.SeasonalData;
      if (data && current in data && current !== SEASON_TS) {
        data[SEASON_TS] = data[current];
        delete data[current];
        renamed++;
      }
    }
  }
  return renamed;
}

// What C# keeps of its own (asserted where expected, never compared with TS).
const OWN = {
  always: ["set match_end:000000000000000000190100"],
};

// Differences decided in the plan or by him; each check asserts the difference, then compares the rest.
const EXPECTED = {
  "dodge-flag-from-elsewhere": {
    why: "a flag that does not name this set does not concede it (decided 2026-10-05): TS marked the set conceded, C# did not",
    check: (ts, cs) => {
      const tsSet = JSON.parse(ts.state[`ranked_set:${SET}`]?.value ?? "null"), csSet = JSON.parse(cs.state[`ranked_set:${SET}`]?.value ?? "null");
      const ok = tsSet?.conceded === true && tsSet?.concedingPlayer === P2 && csSet && !("conceded" in csSet);
      const strip = (run) => { const o = clone(run); delete o.state[`ranked_set:${SET}`]; o.writes = o.writes.filter((w) => !w.startsWith(`set ranked_set:${SET} `)); return o; };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
  "rift-no-set": {
    why: "a rift is not a set (decided 2026-10-05): TS made one from its pending winner, C# ends it with EndOfMatchPayload alone",
    check: (ts, cs) => {
      const ok = ts.state[`ranked_set:${MATCH}`] !== undefined && cs.state[`ranked_set:${MATCH}`] === undefined && cs.state[`ranked_set_pending_winner:${MATCH}`] !== undefined;
      const strip = (run) => {
        const o = clone(run);
        o.state = Object.fromEntries(Object.entries(o.state).filter(([k]) => !/^ranked_set:|^player_ranked_set:|^ranked_set_pending_winner:/.test(k)));
        o.writes = o.writes.filter((w) => !/^set (ranked_set:|player_ranked_set:)|^del ranked_set_pending_winner:/.test(w));
        // TS skipped party preservation for the phantom set's players; there is none here to preserve either way.
        return o;
      };
      return { ok, ts: strip(ts), cs: strip(cs) };
    },
  },
};

function diffRuns(fileA, fileB, current = "Season:SeasonSix") {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  const parts = (x, y) => ["frames", "writes", "state", "mongo"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const name = a.steps[i]?.name ?? b.steps[i]?.name;
    const x = clone(a.steps[i]), y = clone(b.steps[i]);
    // A step that compares nothing is no evidence: every step sends someone something, or writes.
    if (!Object.values(x.frames).some((f) => f.length) && !x.writes.length) {
      differing++;
      console.log(`${name}: TS did nothing`);
    }
    for (const w of OWN.always) {
      if (!y.own.includes(w.split(" ").slice(0, 2).join(" "))) {
        differing++;
        console.log(`${name}: C# did not keep ${w}`);
      }
    }
    // The set's current game (ranked_set_match, C#'s pointer): dropped with a set that is over.
    if (y.own.some((w) => w.startsWith("del ranked_set_match:"))) {
      for (const k of Object.keys(x.state).filter((k) => k.startsWith("ranked_set_match:"))) delete x.state[k];
    }
    const fullRank = seasons(y, current);
    if (Object.values(x.frames).flat().some((f) => f?.data?.template_id === "FullRankUpdate") && fullRank === 0) {
      differing++;
      console.log(`${name}: C# FullRankUpdate is not for ${current}`);
    }
    const expected = EXPECTED[name];
    if (expected) {
      const { ok, ts, cs } = expected.check(x, y);
      const rest = parts(ts, cs);
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
  console.log(differing ? `${differing} difference(s)` : `no differences (${a.steps.length} steps)`);
  process.exit(differing ? 1 : 0);
}

function print(p, x, y) {
  if (p === "frames") {
    for (const id of new Set([...Object.keys(x?.frames ?? {}), ...Object.keys(y?.frames ?? {})])) {
      const fx = JSON.stringify(x?.frames?.[id]), fy = JSON.stringify(y?.frames?.[id]);
      if (fx === fy) continue;
      let at = 0;
      while (at < Math.min(fx?.length ?? 0, fy?.length ?? 0) && fx[at] === fy[at]) at++;
      console.log(`  frames ${id.slice(-2)}: A ...${String(fx).slice(Math.max(0, at - 150), at + 250)}`);
      console.log(`  frames ${id.slice(-2)}: B ...${String(fy).slice(Math.max(0, at - 150), at + 250)}`);
    }
    return;
  }
  if (p === "writes") {
    const only = (from, other) => from.filter((w, i) => from.slice(0, i).filter((v) => v === w).length >= other.filter((v) => v === w).length);
    console.log(`  writes only A: ${JSON.stringify(only(x?.writes ?? [], y?.writes ?? [])).slice(0, 2500)}`);
    console.log(`  writes only B: ${JSON.stringify(only(y?.writes ?? [], x?.writes ?? [])).slice(0, 2500)}`);
    return;
  }
  if (p === "state") {
    for (const k of new Set([...Object.keys(x?.state ?? {}), ...Object.keys(y?.state ?? {})])) {
      if (JSON.stringify(x?.state?.[k]) !== JSON.stringify(y?.state?.[k])) console.log(`  state ${k}: A ${JSON.stringify(x?.state?.[k])?.slice(0, 400)} | B ${JSON.stringify(y?.state?.[k])?.slice(0, 400)}`);
    }
    return;
  }
  console.log(`  ${p} A: ${String(JSON.stringify(x?.[p])).slice(0, 2500)}`);
  console.log(`  ${p} B: ${String(JSON.stringify(y?.[p])).slice(0, 2500)}`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1], args[2]);
else {
  console.error("usage: match_end_diff.mjs run ts|cs <out.json> | diff <ts.json> <cs.json> [Season:Current]");
  process.exit(2);
}
