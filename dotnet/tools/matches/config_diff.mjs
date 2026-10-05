// A match's gameplay config (OnGameplayConfigNotified, and PerksLockedNotification once every player has locked) as the
// TS websocket builds and sends it and as the C# match flow builds and keeps it (GameplayConfigs, Mode On), scenario by
// scenario: what each player's game holds after the match notification and after the perks lock, every Redis write the
// server made beside it (MONITOR), the Redis state after, and the ratings and cosmetics it made in Mongo. Run from the
// repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/config_diff.mjs run ts <out.json>   what the TS websocket (REF_WS_URL) sends to fake games
//   node dotnet/tools/matches/config_diff.mjs run cs <out.json>   what the C# match flow keeps (match_config:{player})
//   node dotnet/tools/matches/config_diff.mjs diff <ts.json> <cs.json>
//
// The TS websocket must be PR #49's code as committed (websocketStart.ts), never a working tree with the bench patch. It
// must not run on the stores during the C# run (it would build too), and the C# match flow not during the TS run; each
// run checks. The C# config is put through the encoder the TS websocket sends with (mvs-dump's HydraEncoder) and decoded
// as a fake game decodes what it is sent, so both sides compare in the form a game receives.
//
// Scratch stores, wiped before every step (never point these at data you want to keep): REF_REDIS_URL, REF_MONGO_URI,
// REF_JWT_SECRET (the TS websocket's JWT_SECRET: the fake games' tokens), as the other harnesses.
import fs from "node:fs";
import { require, need, openScratch, openMonitor, writes, state } from "../refdiff/refdiff.mjs";
import { connectPlayers, decodeFrame } from "../refdiff/gateway.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const { EJSON } = require(process.cwd() + "/node_modules/bson");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const oid = (n) => "00000000000000000017" + String(n).padStart(4, "0");
const [P1, P2, P3, P4] = [1, 2, 3, 4].map(oid);
const [BOT1, BOT2, BOT3] = [11, 12, 13].map(oid);
const [SPEC1, SPEC2] = [21, 22].map(oid);
const HOLDERS = [P1, P2, P3, P4, SPEC1, SPEC2];
const MATCH = oid(100);
const IP = "198.51.100.8";
const token = (pid, n) => jwt.sign({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP }, need("REF_JWT_SECRET"));

// The notification's players, as MatchLauncher writes them.
const human = (playerId, playerIndex, teamIndex) => ({ playerId, partyId: MATCH, playerIndex, teamIndex, isHost: playerIndex === 0, ip: IP, isBot: false });
const bot = (playerId, playerIndex, teamIndex) => ({ playerId, partyId: MATCH, playerIndex, teamIndex, isHost: false, ip: IP, isBot: true });
const watcher = (playerId, i) => ({ playerId, partyId: MATCH, playerIndex: 8888 + i, teamIndex: -1, isHost: false, ip: IP, isSpectator: true });
const notification = (players, extra = {}) => ({ players, matchId: MATCH, matchKey: "the-match-key", map: "M001_V2", mode: "1v1", rollbackPort: 57003, p2p: false, ...extra });

async function run(side, outFile) {
  if (side !== "ts" && side !== "cs") throw new Error("run ts|cs <out.json>");
  const { redis, db, close } = await openScratch("config_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];
  const connect = () => connectPlayers(need("REF_WS_URL"), HOLDERS.map((id, i) => ({ id, token: token(id, i + 1) })));
  let games = side === "ts" ? await connect() : null;

  // A player's session (connections:{id}), as a login and a locked loadout leave it.
  const session = (pid, fields) => redis.hSet(`connections:${pid}`, { id: pid, username: `U-${pid.slice(-2)}`, current_ip: IP, ...fields });
  // The match copy of a player's cosmetics: each field JSON, as the lobby routes write it.
  const matchCosmetics = (pid, fields) => redis.hSet(`connections:${pid}:cosmetics`, Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, typeof v === "string" && v.startsWith("RAW:") ? v.slice(4) : JSON.stringify(v)])));
  const stats = (pid, doc) => db.collection("playerstats").insertOne({ account_id: pid, ...doc });
  const rating = (pid, fields) => db.collection("eloratings").insertOne({ account_id: pid, username: `U-${pid.slice(-2)}`, elo_1v1: 1000, elo_2v2: 1000, wins_1v1: 0, losses_1v1: 0, wins_2v2: 0, losses_2v2: 0, ...fields });
  const COSMETICS = {
    Taunts: { character_C025: { TauntSlots: ["emote_a", "emote_b", "", "emote_d"] }, character_jake: { TauntSlots: ["jake_1", "jake_2", "jake_3", "jake_4"] } },
    StatTrackers: { StatTrackerSlots: ["stattracking_c025wins", "stattracking_c025highestdamagedealt", "stattracking_c025ringouts"] },
    Banner: "banner_one", RingoutVfx: "ring_out_vfx_two", AnnouncerPack: "announcer_x", ProfileIcon: "icon_unused",
  };

  // What each player's game holds now: the last config it was sent (TS), or the one kept for it (C#), as a game decodes it.
  const holds = async () => {
    const out = {};
    for (const id of HOLDERS) {
      if (side === "ts") {
        const configs = games.frames(id).filter((f) => f?.data?.GameplayConfig !== undefined);
        out[id] = configs.length ? configs[configs.length - 1] : null;
      } else {
        const kept = await redis.get(`match_config:${id}`);
        if (kept === null) { out[id] = null; continue; }
        const encoder = new HydraEncoder(true);
        encoder.encodeValue(JSON.parse(kept));
        out[id] = decodeFrame(Buffer.from(encoder.returnValue()));
      }
    }
    return out;
  };
  // Created is the time the config was made: near the step, then set aside.
  const created = (held, started) => {
    for (const frame of Object.values(held)) {
      const config = frame?.data?.GameplayConfig;
      if (!config || !("Created" in config)) continue;
      const numbers = JSON.stringify(config.Created).match(/\d{9,}/g) ?? [];
      const near = numbers.some((n) => Math.abs(Number(n) - started / 1000) < 120 || Math.abs(Number(n) - started) < 120_000);
      config.Created = near ? "<now>" : config.Created;
    }
    return held;
  };

  const steps = [];
  async function step(name, { seed, config, perks = {} }) {
    if (side === "ts") {
      games.close();
      await sleep(400);
      games = await connect();
      await sleep(300);
    }
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    await seed?.();
    games?.clear();
    recording = [];
    const started = Date.now();
    // As MatchLauncher: the notification kept ({match}, read for the spectators at the lock), then published.
    await redis.set(MATCH, JSON.stringify(config), { EX: 1200 });
    await redis.publish("match:notifications", JSON.stringify(config));
    await sleep(1500);
    const built = created(await holds(), started);
    for (const [pid, value] of Object.entries(perks)) {
      if (value !== undefined) await redis.set(`match:${MATCH}:perks:${pid}`, JSON.stringify(value), { EX: 1200 });
    }
    await redis.publish("perks:notifications", JSON.stringify({ containerMatchId: MATCH, playerIds: Object.keys(perks) }));
    await sleep(1200);
    const locked = created(await holds(), started);
    const lines = recording;
    recording = null;
    // Each run checks that the other server is not on these stores: only the TS websocket reads player:{id} (its unused
    // redisGetPlayers); only the C# match flow keeps match_config.
    if (side === "cs" && lines.some((l) => /"hgetall" "player:/i.test(l))) throw new Error("the TS websocket is running on these stores");
    // Left out: each server's own bookkeeping, not the config's (the C# instance registry; the TS websocket's presence for
    // the fake games: heartbeats, online players, active_ip_accounts at each pong).
    const all = writes(lines, self).filter((w) => !/^(set|zadd|zrem|del) ovs:instance|^zadd player_heartbeats|^(sadd|srem) online_players|^(zadd|expire|zremrangebyscore) active_ip_accounts:/.test(w)).map((w) => w.replace(/\\(["\\])/g, "$1"));
    const kept = all.filter((w) => w.startsWith("set match_config:"));
    if (side === "ts" && kept.length) throw new Error("the C# match flow is running on these stores");
    const mongo = {};
    for (const c of ["eloratings", "cosmetics", "playerstats"]) {
      const docs = await db.collection(c).find({}, { promoteValues: false, sort: { account_id: 1 } }).toArray();
      mongo[c] = JSON.parse(EJSON.stringify(docs.map(({ _id, ...rest }) => ({ _id: _id instanceof ObjectId && HOLDERS.includes(_id.toHexString()) ? _id.toHexString() : "<made>", ...rest })), { relaxed: false }));
      for (const d of mongo[c]) if (d.updated_at) d.updated_at = "<set>";
    }
    steps.push({
      name,
      built,
      locked,
      kept: [...new Set(kept.map((w) => w.split(" ")[1]))].sort(),
      writes: all.filter((w) => !w.startsWith("set match_config:") && !w.startsWith("publish ")).sort(),
      // ... and in the state, also the C# match flow's results stream (its consumer group is made again after a flush).
      state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^match_config:|^ovs:instance|^player_heartbeats$|^online_players$|^active_ip_accounts:|^match:results$/.test(k))),
      mongo,
    });
    const got = Object.entries(locked).filter(([, f]) => f).map(([id]) => id.slice(-2));
    console.log(`${name}: configs held by ${got.join(",") || "nobody"}`);
  }

  const ranked1v1 = () => notification([human(P1, 0, 0), human(P2, 1, 1)]);
  await step("ranked-1v1", {
    seed: async () => {
      await session(P1, { character: "character_C025", skin: "skin_c025_default", profileIcon: "profile_icon_c025", GameplayPreferences: "0" });
      await session(P2, { character: "character_jake", skin: "skin_jake_default" });
      await matchCosmetics(P1, COSMETICS);
      // P2 has no match copy: their equipped cosmetics (cached), written back.
      await redis.set(`player:${P2}:cosmetics`, JSON.stringify({ ...COSMETICS, Banner: "banner_cached", StatTrackers: { StatTrackerSlots: ["stat_tracking_bundle_iron_giant_wins", "stat_tracking_bundle_default", "stat_tracking_bundle_arya_ringouts"] } }));
      await stats(P1, { characters_1v1: { character_C025: { wins: 40, highestDamageDealt: 300.5, ringouts: 12 }, character_jake: { wins: 3 } } });
      // Iron Giant (C017) is stored under its alias, creature.
      await stats(P2, { characters_2v2: { character_creature: { wins: 7 }, character_arya: { ringouts: 5 } } });
      await rating(P1, { characters_1v1: { character_C025: { elo: 1234 } } });
    },
    config: ranked1v1(),
    perks: { [P1]: ["perk_a", "perk_b"], [P2]: [] },
  });
  await step("both-modes", {
    seed: async () => {
      await session(P1, { character: "character_C025", skin: "skin_c025_default", profileIcon: "profile_icon_c025" });
      await session(P2, { character: "character_jake", skin: "skin_jake_default" });
      await matchCosmetics(P1, COSMETICS);
      await matchCosmetics(P2, COSMETICS);
      await stats(P1, { characters_1v1: { character_C025: { wins: 397, highestDamageDealt: 621, ringouts: 2953 } }, characters_2v2: { character_C025: { wins: 5, highestDamageDealt: 281, ringouts: 28 }, character_c025: { wins: 2 } } });
    },
    config: ranked1v1(),
    perks: { [P1]: ["perk_a"], [P2]: ["perk_c"] },
  });
  await step("ranked-2v2-tiers", {
    seed: async () => {
      for (const [pid, prefs] of [[P1, "abc"], [P2, "-3"], [P3, "991"], [P4, undefined]]) {
        await session(pid, { character: "character_jake", skin: "skin_jake_default", ...(prefs === undefined ? {} : { GameplayPreferences: prefs }) });
        await matchCosmetics(pid, { Banner: "b" });
      }
      // Below every tier; the top of Master; a fighter's own rating over the mode's; exactly a tier's lowest.
      await rating(P1, { elo_2v2: -5 });
      await rating(P2, { elo_2v2: 2999 });
      await rating(P3, { elo_2v2: 3000, characters_2v2: { character_jake: { elo: 1499 } }, characters_1v1: { character_jake: { elo: 2500 } } });
      await rating(P4, { elo_2v2: 1500 });
    },
    config: notification([human(P1, 0, 0), human(P2, 1, 0), human(P3, 2, 1), human(P4, 3, 1)], { mode: "2v2", map: "M001" }),
    perks: { [P1]: ["p1"], [P2]: ["p2"], [P3]: ["p3"], [P4]: ["p4"] },
  });
  await step("casual-bots", {
    seed: async () => {
      await session(P1, { character: "character_jake", skin: "skin_jake_default" });
      await matchCosmetics(P1, COSMETICS);
      await redis.hSet(`bot_config:${BOT1}`, { character: "character_arya", skin: "skin_arya_default", difficultyMin: "3", difficultyMax: "3" });
      await redis.set(`match:${MATCH}:perks:${BOT1}`, "[]");
      await redis.set(`match:${MATCH}:perks:${BOT2}`, JSON.stringify(["perk_launch"]));
    },
    config: notification([human(P1, 0, 0), bot(BOT1, 1, 1), bot(BOT2, 2, 1)], { mode: "2v2", isCustomGame: true, gameplayConfigOverride: { bIsCustomGame: false } }),
    perks: { [P1]: ["perk_a"], [BOT1]: undefined, [BOT2]: undefined },
  });
  await step("custom-ffa-spectators", {
    seed: async () => {
      for (const pid of [P1, P2, P3]) await session(pid, { character: "character_jake", skin: "skin_jake_default" });
      await session(SPEC1, { character: "character_finn" });
      for (const pid of [P1, P2, P3, SPEC1]) await matchCosmetics(pid, COSMETICS);
      // SPEC2 has no session and no cosmetics anywhere: defaults, and a cosmetics document made.
    },
    config: notification([human(P1, 0, 0), human(P2, 1, 1), human(P3, 2, 2), watcher(SPEC1, 0), watcher(SPEC2, 1)], {
      mode: "ffa", isCustomGame: true, customMatchTime: 300, customNumRingouts: 5, customHazards: true, customShields: false,
      playerBuffs: { [P1]: ["buff_x"] }, worldBuffs: ["world_y"],
      // A spectator's override is not applied (TS overrides Players only).
      playerConfigOverrides: { [P2]: { Handicap: 1 }, [SPEC1]: { Handicap: 9 } },
    }),
    perks: { [P1]: ["a"], [P2]: ["b"], [P3]: ["c"] },
  });
  // As RiftMatchService.Build launches a rift node with two enemy bots and a friendly one, retried (its template and data):
  // no isCustomGame (TS looks a rank up), the config's PvP fields replaced, every player's look replaced.
  const riftBot = (character) => ({ Character: character, Skin: `skin_${character.slice(10)}_default`, StartingDamage: 30, Banner: "banner_default",
    ProfileIcon: "profile_icon_default_gold", RingoutVfx: "ring_out_vfx_default", BotBehaviorOverride: "bot_behavior_boss", bUseCharacterDisplayName: true,
    Username: {}, Buffs: ["buff_enemy"], Perks: [], BotDifficultyMin: 0, BotDifficultyMax: 0 });
  await step("rift-retry", {
    seed: async () => {
      await session(P1, { character: "character_jake", skin: "skin_jake_default" });
      await matchCosmetics(P1, COSMETICS);
    },
    config: notification([human(P1, 0, 0), bot(BOT2, 1, 1), bot(BOT1, 2, 0), bot(BOT3, 3, 1)], {
      mode: "2v2", map: "M015_V1",
      gameplayConfigOverride: {
        bIsRift: true, bIsPvP: false, bIsRanked: false, bIsCustomGame: false, bIsCasualSpecial: false, bModeGrantsProgress: false, bIsTutorial: false,
        bAllowMapHazards: true, RiftNodeId: "rift_node_x", RiftNodeAttunement: "Attunements:Fire", CountdownDisplay: "CountdownTypes:XvY",
        HudSettings: { bDisplayPortraits: true, bDisplayStocks: true, bDisplayTimer: false }, WorldBuffs: ["world_buff"], Map: "M015_V1",
        ScoreEvaluationRule: "TargetScoreIsLoss", ScoreAttributionRule: "AttributeToVictim", MatchDurationSeconds: 240, ModeString: "2v2",
        TeamData: [{ TargetScore: 2 }, { TargetScore: 3 }],
      },
      playerConfigOverrides: {
        [P1]: { Character: "character_jake", Skin: "skin_jake_rift", Buffs: ["buff_friendly"] },
        [BOT2]: riftBot("character_arya"), [BOT1]: riftBot("character_finn"), [BOT3]: riftBot("character_C017"),
      },
      gameplayConfigTemplate: "RiftRetryNotification",
      gameplayConfigData: { PriorMatchId: oid(99) },
    }),
    perks: { [P1]: ["perk_r"], [BOT1]: undefined, [BOT2]: undefined, [BOT3]: undefined },
  });
  await step("map-pve03", {
    seed: async () => { for (const pid of [P1, P2]) { await session(pid, { character: "character_jake" }); await matchCosmetics(pid, { Banner: "b" }); } },
    config: notification([human(P1, 0, 0), human(P2, 1, 1)], { map: "PVE_03" }),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("no-players-after-override", {
    seed: async () => { for (const pid of [P1, P2]) { await session(pid, { character: "character_jake" }); await matchCosmetics(pid, { Banner: "b" }); } },
    config: notification([human(P1, 0, 0), human(P2, 1, 1)], { gameplayConfigOverride: { Players: {} } }),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("no-map", {
    seed: async () => { for (const pid of [P1, P2]) await session(pid, { character: "character_jake" }); },
    config: (() => { const c = notification([human(P1, 0, 0), human(P2, 1, 1)]); delete c.map; return c; })(),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("fallback-tracker-not-text", {
    seed: async () => {
      await session(P1, { character: "character_C025", skin: "skin_c025_default", profileIcon: "profile_icon_c025", GameplayPreferences: "12" });
      await session(P2, { character: "character_jake" });
      await matchCosmetics(P1, { ...COSMETICS, StatTrackers: { StatTrackerSlots: ["stattracking_c025wins", 5, "stattracking_c025ringouts"] } });
      await matchCosmetics(P2, { ...COSMETICS, StatTrackers: { StatTrackerSlots: "abc" } });
      await rating(P1, { elo_1v1: 1700 });
    },
    config: ranked1v1(),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("fallback-null-stats", {
    seed: async () => {
      await session(P1, { character: "character_C025", profileIcon: "profile_icon_c025" });
      await session(P2, { character: "character_jake" });
      await matchCosmetics(P1, COSMETICS);
      await matchCosmetics(P2, { ...COSMETICS, StatTrackers: { StatTrackerSlots: ["stat_tracking_bundle_iron_giant_wins", "stat_tracking_bundle_default", "stat_tracking_bundle_default"] } });
      await stats(P1, { characters_1v1: { character_C025: null } });
      // A null entry for a fighter none of P2's trackers counts is never read: P2 keeps their config.
      await stats(P2, { characters_1v1: { character_C025: null, character_C017: { wins: 1 } } });
    },
    config: ranked1v1(),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("cosmetics-not-json", {
    seed: async () => {
      await session(P1, { character: "character_jake" });
      await session(P2, { character: "character_jake" });
      await matchCosmetics(P1, { ...COSMETICS, Banner: "RAW:not json", RingoutVfx: null, Taunts: "RAW:{broken" });
      // Falsy taunt slots: the default ones.
      await matchCosmetics(P2, { Taunts: { character_jake: { TauntSlots: "" } }, StatTrackers: { StatTrackerSlots: ["stattracking_c025wins"] } });
    },
    config: ranked1v1(),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });
  await step("perks-partial", {
    // P1's taunt slots are an empty list: truthy, so kept as they are.
    seed: async () => { for (const pid of [P1, P2]) { await session(pid, { character: "character_jake" }); await matchCosmetics(pid, { Banner: "b", Taunts: { character_jake: { TauntSlots: [] } } }); } },
    config: ranked1v1(),
    // P2 is in the lock but never stored perks; P3 is in no config.
    perks: { [P1]: ["x"], [P2]: undefined, [P3]: ["z"] },
  });
  await step("spectator-lock-no-perks", {
    seed: async () => { for (const pid of [P1, SPEC1]) { await session(pid, { character: "character_jake" }); await matchCosmetics(pid, { Banner: "b" }); } },
    config: notification([human(P1, 0, 0), watcher(SPEC1, 0)], { isCustomGame: true }),
    // Nobody's perks merge; a spectator holding the config still makes it PerksLockedNotification.
    perks: { [P1]: undefined },
  });
  await step("no-session", {
    seed: async () => { await session(P1, { character: "character_jake" }); await matchCosmetics(P1, { Banner: "b" }); },
    config: ranked1v1(),
    perks: { [P1]: ["x"], [P2]: ["y"] },
  });

  games?.close();
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ side, steps }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
  await close();
}

const clone = (v) => JSON.parse(JSON.stringify(v));

// Stat trackers count a fighter's entries of both modes in C# (decided 2026-10-05); TS let 2v2 replace 1v1. P1 plays
// C025 with 397/621/2953 in 1v1, 5/281/28 and a second entry (c025: 2 wins) in 2v2: C# 404 wins, 621 highest, 2981
// ring-outs; TS 7, 281, 28 (its spread keeps both 2v2 keys, character_C025 and character_c025).
function bothModes(ts, cs) {
  const values = (run, phase) => run[phase][P1]?.data?.GameplayConfig?.Players?.[P1]?.StatTrackers?.map(([, v]) => v);
  const ok = ["built", "locked"].every((phase) => JSON.stringify(values(ts, phase)) === "[7,281,28]" && JSON.stringify(values(cs, phase)) === "[404,621,2981]"
    && JSON.stringify(values(ts, phase)) === JSON.stringify(ts[phase][P2]?.data?.GameplayConfig?.Players?.[P1]?.StatTrackers?.map(([, v]) => v))
    && JSON.stringify(values(cs, phase)) === JSON.stringify(cs[phase][P2]?.data?.GameplayConfig?.Players?.[P1]?.StatTrackers?.map(([, v]) => v)));
  const strip = (run) => {
    const o = clone(run);
    for (const phase of ["built", "locked"]) for (const frame of Object.values(o[phase])) for (const t of frame?.data?.GameplayConfig?.Players?.[P1]?.StatTrackers ?? []) t[1] = "<value>";
    return o;
  };
  return { ok, ts: strip(ts), cs: strip(cs) };
}

const EXPECTED = {
  "both-modes": { why: "stat trackers count both modes' entries in C#; TS showed only the 2v2 ones", check: bothModes },
};

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  let differing = 0;
  const parts = (x, y) => ["built", "locked", "writes", "state", "mongo"].filter((p) => JSON.stringify(x?.[p]) !== JSON.stringify(y?.[p]));
  for (let i = 0; i < Math.max(a.steps.length, b.steps.length); i++) {
    const name = a.steps[i]?.name ?? b.steps[i]?.name;
    const x = a.steps[i], y = b.steps[i];
    // C# keeps a config for exactly the players TS sent one to (every human and spectator here is connected).
    const sentTs = Object.entries(x?.built ?? {}).filter(([, f]) => f).map(([id]) => `match_config:${id}`).sort();
    if (JSON.stringify(sentTs) !== JSON.stringify(y?.kept)) {
      differing++;
      console.log(`${name}: TS sent configs to ${JSON.stringify(sentTs)}, C# kept ${JSON.stringify(y?.kept)}`);
    }
    // A step that compares nothing is no evidence.
    if (!Object.values(x?.built ?? {}).some(Boolean) && !["no-players-after-override", "no-map"].includes(name)) {
      differing++;
      console.log(`${name}: TS sent nobody a config`);
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
  if (p === "built" || p === "locked") {
    for (const id of new Set([...Object.keys(x?.[p] ?? {}), ...Object.keys(y?.[p] ?? {})])) {
      const fx = JSON.stringify(x?.[p]?.[id]), fy = JSON.stringify(y?.[p]?.[id]);
      if (fx === fy) continue;
      // The first place the two differ, with some context.
      let at = 0;
      while (at < Math.min(fx?.length ?? 0, fy?.length ?? 0) && fx[at] === fy[at]) at++;
      console.log(`  ${p} ${id}: A ...${String(fx).slice(Math.max(0, at - 150), at + 250)}`);
      console.log(`  ${p} ${id}: B ...${String(fy).slice(Math.max(0, at - 150), at + 250)}`);
    }
    return;
  }
  console.log(`  ${p} A: ${String(JSON.stringify(x?.[p])).slice(0, 3000)}`);
  console.log(`  ${p} B: ${String(JSON.stringify(y?.[p])).slice(0, 3000)}`);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: config_diff.mjs run ts|cs <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
