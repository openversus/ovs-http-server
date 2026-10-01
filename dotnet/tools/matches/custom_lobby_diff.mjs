// The custom lobby SSC routes on the TS server and the C# port, scenario by scenario: each scenario is a sequence of
// requests from up to five players against fresh stores, and every request's answer, the Redis writes the server made
// (MONITOR, the Lua scripts' own included), what each player's game was sent on the websocket, and the state after are
// recorded. Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/matches/custom_lobby_diff.mjs run <baseUrl> <out.json> [scenario name filter]
//   node dotnet/tools/matches/custom_lobby_diff.mjs diff <ts.json> <cs.json>
//
// Scratch stores, wiped before every scenario (never point these at data you want to keep):
//   REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET  as for the other harnesses
//   REF_WS_URL   the TS websocket on the same scratch stores: it delivers for both servers (the TS server's
//                custom_lobby:notification, the port's ws:send), so the games' frames are compared, not the channels
//   SKIP_MODES=1 leaves out the scenario that selects every game mode (70 requests the TS server answers after 1.5 s)
//
// The lobby is JSON that Lua's cjson rewrites on every change, in its own key order: objects are compared by content
// (keys sorted), here and in the writes and state. cjson also writes an empty array as {} (every reader repairs it,
// fixCjsonEmptyTables): in the lobby's stored JSON (custom_lobby_ssc:*, its writes and state; never the answers,
// frames and published messages) an empty array and an empty object read the same. Fresh ids, times, lobby codes, match keys and rollback ports are
// replaced by placeholders once their format is checked. A request the TS server never answers shows as "<no answer>".
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
const PLAYERS = [1, 2, 3, 4, 5].map(oid);
const [P1, P2, P3, P4, P5] = PLAYERS;
const IP = "198.51.100.7";
const CODE = /^[A-HJ-NP-Z2-9]{5}$/;
// Channels whose TS websocket handlers keep per-connection state (docs/REALTIME.md): compared as writes. The rest
// (custom_lobby:notification) are compared by the frames they turn into.
const STATEFUL = new Set(["matchmaking:cancel", "party:queued", "match:notifications", "matchmaking:complete", "perks:notifications", "match:end", "lobby:rejoin"]);

// The fields the game sends with lobby requests (custom-bots-0930 capture).
const COMMON = { AutoPartyPreference: false, CrossplayPreference: 1, GameplayPreferences: 448, HissCrc: 12, LobbyTemplate: "custom_game_lobby", Platform: "PC", Version: "CLIENT:2FAE7-Retail DATA:1 PERKS:1" };
const MULTIPLAY = {
  "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
  "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
  "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
  "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
};
const ARYA = { CharacterAssetPath: "/Game/Panda_Main/Characters/Arya/character_C006.character_C006", CharacterSlug: "character_arya", SkinAssetPath: "/Game/Panda_Main/Characters/Arya/Skins/S14/C006_S14.C006_S14", SkinSlug: "skin_arya_s08" };
const TAZ = { CharacterAssetPath: "/Game/Panda_Main/Characters/Taz/character_C010.character_C010", CharacterSlug: "character_taz", SkinAssetPath: "/Game/Panda_Main/Characters/Taz/Skins/S01/C010_S01.C010_S01", SkinSlug: "skin_taz_default" };
const BOT = (n) => `Bot${String(n).padStart(32, "0")}`;
const token = (pid) => {
  const n = PLAYERS.indexOf(pid) + 1;
  return jwt.sign({ id: pid, profile_id: oid(900 + n), wb_network_id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, current_ip: IP }, need("REF_JWT_SECRET"));
};

async function request(baseUrl, method, path, pid, body) {
  let payload;
  if (body !== undefined) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
  }
  let response, bytes;
  try {
    response = await fetch(`${baseUrl}${path}`, {
      method,
      headers: { "content-type": "application/x-ag-binary", "x-hydra-access-token": token(pid), "x-real-ip": IP },
      body: payload,
      signal: AbortSignal.timeout(6000),
    });
    bytes = Buffer.from(await response.arrayBuffer());
  } catch (e) {
    return { status: `<no answer: ${e.name}>` };
  }
  let decoded;
  try {
    decoded = bytes.length ? new HydraDecoder(bytes).readValue() : "";
  } catch {
    decoded = bytes.toString("utf8");
  }
  return { status: response.status, body: decoded };
}

// ── The scenarios ────────────────────────────────────────────────────────────────────────────────────────────────────
// Each gets `t`: t.ssc(label, route, pid, body) and t.get(label, path, pid) record a request; t.probe(label, value)
// records a value the harness measured; t.redis / t.db are the stores (setup only); t.lobby is the last lobby id seen.

const create = (t, pid = P1, label = "create") => t.ssc(label, "create_custom_game_lobby", pid, { ...COMMON, AllMultiplayParams: MULTIPLAY, LobbyType: 0 });
const join = (t, pid, spectator = false, label = `join ${pid.slice(-1)}${spectator ? " as spectator" : ""}`) => t.ssc(label, "join_custom_game_lobby", pid, { HostId: t.lobby, IsSpectator: spectator });
const style = (t, s, pid = P1) => t.ssc(`team style ${s}${pid === P1 ? "" : " by " + pid.slice(-1)}`, "update_team_style_for_custom_game", pid, { MatchID: t.lobby, TeamStyle: s });
const mode = (t, slug, pid = P1) => t.ssc(`game mode ${slug}${pid === P1 ? "" : " by " + pid.slice(-1)}`, "set_game_mode_for_custom_game", pid, { MatchID: t.lobby, GameModeSlug: slug });
const sw = (t, pid, team) => t.ssc(`switch ${pid.slice(-1)} to ${team}`, "switch_custom_game_lobby_team", pid, { MatchID: t.lobby, TeamIndex: team });
const ready = (t, pid, r) => t.ssc(`${r ? "ready" : "unready"} ${pid.slice(-1)}`, "set_ready_for_lobby", pid, { ...COMMON, LobbyId: t.lobby, MatchID: t.lobby, Ready: r });
const leave = (t, pid, lobbyId = t.lobby) => t.ssc(`leave ${pid.slice(-1)}`, "leave_player_lobby", pid, { ...COMMON, LobbyId: lobbyId });
const bot = (t, n, team, extra = {}, pid = P1) => t.ssc(`add bot ${n} to ${team}${pid === P1 ? "" : " by " + pid.slice(-1)}`, "add_custom_game_bot", pid, { BotAccountID: BOT(n), BotSettingSlug: "Medium", ...ARYA, MatchID: t.lobby, TeamIndex: team, ...extra });
const oneMap = (t, map = "M001") => t.ssc(`maps ${map}`, "set_enabled_maps_for_custom_game", P1, { MapSlugs: [map], MatchID: t.lobby });
const start = (t, pid = P1) => t.ssc(`start${pid === P1 ? "" : " by " + pid.slice(-1)}`, "start_custom_match", pid, {
  ...COMMON, BotData: {}, ClusterID: "ec2-us-east-1-dokken", LobbyId: t.lobby, MatchID: t.lobby, MultiplayProfileID: "1252499", MultiplayRegionID: "", MultiplayRegionSearchID: 1,
});
const lobbyJson = async (t) => JSON.parse(await t.redis.get(`custom_lobby_ssc:${t.lobby}`));
const editLobby = async (t, change) => {
  const l = await lobbyJson(t);
  change(l);
  await t.redis.set(`custom_lobby_ssc:${t.lobby}`, JSON.stringify(l), { EX: 172800 });
};

const SCENARIOS = {
  // Creating
  "create": async (t) => { await create(t); },
  "create-zero-preferences": async (t) => { await t.redis.hSet(`connections:${P1}`, "GameplayPreferences", "0"); await create(t); },
  "create-bare-record": async (t) => { await t.db.collection("playertesters").updateOne({ _id: new ObjectId(P1) }, { $unset: { variant: "", profile_icon: "", character: "" } }); await create(t); },
  "create-no-session": async (t) => { await t.redis.del(`connections:${P1}`); await create(t); },
  "create-no-player-hash": async (t) => { await t.redis.del(`player:${P1}`); await create(t); },
  // The game's next requests after a create (custom-bots-0930 capture): the party route set_lobby_joinable, with the
  // custom lobby's id.
  "create-then-joinable": async (t) => { await create(t); await t.ssc("joinable", "set_lobby_joinable", P1, { ...COMMON, LobbyId: t.lobby }); },
  "create-twice": async (t) => { await create(t, P1, "create 1"); await create(t, P1, "create 2"); },
  "create-party-lobby-while-in-custom": async (t) => { await create(t); await t.ssc("create_party_lobby", "create_party_lobby", P1, { ...COMMON, AllMultiplayParams: MULTIPLAY, LobbyType: 0, LobbyTemplate: "party_lobby" }); },

  // Joining
  "join": async (t) => { await create(t); await join(t, P2); await join(t, P3); await join(t, P4); await join(t, P5); },
  "join-spectator": async (t) => { await create(t); await join(t, P2, true); },
  "join-solos-full-goes-to-spectators": async (t) => { await create(t); await style(t, "Solos"); await join(t, P2); await join(t, P3); },
  "join-ffa": async (t) => { await create(t); await style(t, "FFA"); await join(t, P2); await join(t, P3); await join(t, P4); await join(t, P5); },
  "join-missing-lobby": async (t) => { t.lobby = oid(777); await join(t, P2); },
  "join-not-joinable": async (t) => { await create(t); await editLobby(t, (l) => { l.IsLobbyJoinable = false; }); await join(t, P2); },
  "join-zero-preferences-no-session": async (t) => { await create(t); await t.redis.hSet(`connections:${P2}`, "GameplayPreferences", "0"); await join(t, P2); await t.redis.del(`connections:${P3}`); await join(t, P3); },
  // Known bug: a player who joins again is added again (the script never looks for them).
  "join-twice": async (t) => { await create(t); await join(t, P2, false, "join 2"); await join(t, P2, false, "join 2 again"); await ready(t, P2, true); await ready(t, P1, true); },
  "join-own-lobby": async (t) => { await create(t); await join(t, P1); },
  "rejoin-after-start": async (t) => { await create(t); await join(t, P2); await oneMap(t); await start(t); await join(t, P2, false, "join 2 after the match"); await ready(t, P2, true); },

  // Settings
  "settings": async (t) => {
    await create(t); await join(t, P2);
    await style(t, "Solos");
    await t.ssc("int setting hazards", "update_int_setting_for_custom_game", P1, { MatchID: t.lobby, SettingKey: "AllowHazards", SettingValue: 0 });
    await t.ssc("int setting ringouts", "update_int_setting_for_custom_game", P1, { MatchID: t.lobby, SettingKey: "NumRingoutsForWin", SettingValue: 5 });
    await t.ssc("maps", "set_enabled_maps_for_custom_game", P1, { MapSlugs: ["M001_V2", "M003_V1", "not_a_map"], MatchID: t.lobby });
    await t.ssc("world buffs", "set_world_buffs_for_custom_game", P1, { MatchID: t.lobby, WorldBuffSlugs: ["buff_low_gravity", "buff_low_gravity"] });
    await t.ssc("handicap own", "set_player_handicap_for_custom_game", P1, { MatchID: t.lobby, PlayerHandicap: 2, PlayerId: P1 });
    await t.ssc("handicap other", "set_player_handicap_for_custom_game", P1, { MatchID: t.lobby, PlayerHandicap: 3, PlayerId: P2 });
    await t.ssc("handicap by member", "set_player_handicap_for_custom_game", P2, { MatchID: t.lobby, PlayerHandicap: 1, PlayerId: P2 });
    await mode(t, "gm_infinitejumps");
    await t.ssc("reset", "reset_custom_lobby_to_defaults", P1, { MatchID: t.lobby });
  },
  "settings-by-member": async (t) => {
    await create(t); await join(t, P2);
    await style(t, "Solos", P2);
    await t.ssc("int setting by 2", "update_int_setting_for_custom_game", P2, { MatchID: t.lobby, SettingKey: "AllowHazards", SettingValue: 0 });
    await t.ssc("maps by 2", "set_enabled_maps_for_custom_game", P2, { MapSlugs: ["M001"], MatchID: t.lobby });
    await t.ssc("world buffs by 2", "set_world_buffs_for_custom_game", P2, { MatchID: t.lobby, WorldBuffSlugs: [] });
    await mode(t, "gm_classic_1v1", P2);
    await t.ssc("reset by 2", "reset_custom_lobby_to_defaults", P2, { MatchID: t.lobby });
  },
  "settings-missing-lobby": async (t) => {
    t.lobby = oid(777);
    await style(t, "Solos");
    await t.ssc("int setting", "update_int_setting_for_custom_game", P1, { MatchID: t.lobby, SettingKey: "AllowHazards", SettingValue: 0 });
    await t.ssc("maps", "set_enabled_maps_for_custom_game", P1, { MapSlugs: ["M001"], MatchID: t.lobby });
    await t.ssc("world buffs", "set_world_buffs_for_custom_game", P1, { MatchID: t.lobby, WorldBuffSlugs: [] });
    await t.ssc("handicap", "set_player_handicap_for_custom_game", P1, { MatchID: t.lobby, PlayerHandicap: 2, PlayerId: P1 });
    await t.ssc("reset", "reset_custom_lobby_to_defaults", P1, { MatchID: t.lobby });
    await sw(t, P1, 1);
    await bot(t, 1, 1);
    await t.ssc("promote", "promote_to_lobby_leader", P1, { MatchID: t.lobby, PromoteTarget: P2 });
    await t.ssc("kick", "kick_from_lobby", P1, { MatchID: t.lobby, KickeeAccountID: P2 });
    await t.ssc("lobby code", "lobby_code", P1, { LobbyId: t.lobby });
    await start(t);
  },
  "team-style-solos-with-three": async (t) => { await create(t); await join(t, P2); await join(t, P3); await style(t, "Solos"); },
  "team-style-ffa-then-duos": async (t) => { await create(t); await join(t, P2); await join(t, P3); await style(t, "FFA"); await style(t, "Duos"); await style(t, "Other"); },
  "game-mode-buffs": async (t) => { await create(t); await join(t, P2); await join(t, P3); await mode(t, "gm_infinitejumps"); await sw(t, P2, 1); },
  "game-mode-unknown": async (t) => { await create(t); await mode(t, "gm_not_a_mode"); },
  "game-mode-without-teams": async (t) => { await create(t); await mode(t, "classic_game_mode"); },
  "reset-without-mode": async (t) => { await create(t); await editLobby(t, (l) => { delete l.GameModeSlug; }); await t.ssc("reset", "reset_custom_lobby_to_defaults", P1, { MatchID: t.lobby }); },

  // Bots
  "bots": async (t) => {
    await create(t); await join(t, P2);
    await bot(t, 1, 1);
    await t.ssc("bot fighter", "update_custom_game_bot_fighter", P1, { MatchID: t.lobby, BotAccountID: BOT(1), BotSettingSlug: "Hard", ...TAZ });
    await bot(t, 2, 4);
    await bot(t, 3, 7);
    await bot(t, 4, 1, {}, P2);
    await t.ssc("bot fighter by 2", "update_custom_game_bot_fighter", P2, { MatchID: t.lobby, BotAccountID: BOT(1), BotSettingSlug: "Easy", ...TAZ });
    await t.ssc("bot fighter missing bot", "update_custom_game_bot_fighter", P1, { MatchID: t.lobby, BotAccountID: BOT(9), BotSettingSlug: "Easy", ...TAZ });
    await ready(t, P1, true); await ready(t, P2, true);
    await leave(t, P2);
  },

  // Team switching. Known bug: players cannot move between the spectators and the players.
  "switch-duos": async (t) => { await create(t); await join(t, P2); await join(t, P3); await sw(t, P2, 1); await sw(t, P2, 1); await sw(t, P2, 4); await sw(t, P2, 0); await sw(t, P3, 4); await sw(t, P3, 1); await sw(t, P1, 2); await sw(t, P1, 9); },
  "switch-solos": async (t) => { await create(t); await style(t, "Solos"); await join(t, P2); await join(t, P3); await sw(t, P2, 0); await sw(t, P2, 4); await sw(t, P3, 1); await sw(t, P1, 1); await sw(t, P2, 1); },
  "switch-ffa": async (t) => { await create(t); await style(t, "FFA"); await join(t, P2); await sw(t, P2, 4); await sw(t, P2, 3); await sw(t, P2, 2); },
  "switch-other": async (t) => { await create(t); await mode(t, "gm_targetbreak"); await join(t, P2, true); await sw(t, P2, 1); await sw(t, P2, 0); await sw(t, P1, 4); },
  "switch-spectators-full": async (t) => { await create(t); await style(t, "Solos"); for (const p of [P2, P3, P4, P5]) await join(t, p, true); await sw(t, P1, 4); },
  "switch-not-in-lobby": async (t) => { await create(t); await sw(t, P2, 1); },

  // Kicking and promoting
  "kick": async (t) => { await create(t); await join(t, P2); await join(t, P3); await ready(t, P2, true); await t.ssc("kick 2", "kick_from_lobby", P1, { MatchID: t.lobby, KickeeAccountID: P2 }); await t.ssc("kick self", "kick_from_lobby", P1, { MatchID: t.lobby, KickeeAccountID: P1 }); await t.ssc("kick by 3", "kick_from_lobby", P3, { MatchID: t.lobby, KickeeAccountID: P1 }); await t.ssc("kick absent", "kick_from_lobby", P1, { MatchID: t.lobby, KickeeAccountID: P4 }); await bot(t, 1, 1); await t.ssc("kick bot", "kick_from_lobby", P1, { MatchID: t.lobby, KickeeAccountID: BOT(1) }); },
  "promote": async (t) => { await create(t); await join(t, P2); await join(t, P3); await t.ssc("promote 2", "promote_to_lobby_leader", P1, { MatchID: t.lobby, PromoteTarget: P2 }); await ready(t, P1, false); await t.ssc("promote by 3", "promote_to_lobby_leader", P3, { MatchID: t.lobby, PromoteTarget: P3 }); await t.ssc("promote absent", "promote_to_lobby_leader", P3, { MatchID: t.lobby, PromoteTarget: P4 }); },

  // Ready. Known bug: a lobby gets stuck, players cannot unready.
  "ready": async (t) => { await create(t); await join(t, P2); await join(t, P3, true); await ready(t, P2, true); await ready(t, P2, true); await ready(t, P2, false); await ready(t, P2, false); await ready(t, P3, true); await ready(t, P2, true); await ready(t, P1, false); await ready(t, P1, true); },
  "ready-after-promote-and-leave": async (t) => { await create(t); await join(t, P2); await join(t, P3); await ready(t, P2, true); await t.ssc("promote 3", "promote_to_lobby_leader", P1, { MatchID: t.lobby, PromoteTarget: P3 }); await ready(t, P1, false); await ready(t, P3, false); await leave(t, P3); await ready(t, P2, false); },

  // Leaving. Known bug: when the leader leaves, the role does not pass to the longest-standing member.
  "leave-member": async (t) => { await create(t); await join(t, P2); await ready(t, P2, true); await leave(t, P2); },
  "leave-leader-earliest-on-later-team": async (t) => { await create(t); await join(t, P2); await join(t, P3); await sw(t, P2, 1); await sw(t, P3, 0); await leave(t, P1); },
  "leave-leader-earliest-spectating": async (t) => { await create(t); await join(t, P2, true); await join(t, P3); await join(t, P4); await leave(t, P1); },
  "leave-leader-bot-before-human": async (t) => { await create(t); await bot(t, 1, 0); await join(t, P2); await leave(t, P1); },
  // A hypothesis: LobbyPlayerIndex is the count at join, so a join after a leave can reuse an index.
  "join-after-leave-index": async (t) => { await create(t); await join(t, P2); await join(t, P3); await leave(t, P2); await join(t, P4); await bot(t, 1, 1); },
  "leave-last-with-code": async (t) => { await create(t); await t.ssc("lobby code", "lobby_code", P1, { LobbyId: t.lobby }); await leave(t, P1); },
  "leave-stale-lobby-id": async (t) => { await create(t); await join(t, P2); await leave(t, P2, oid(555)); },
  "leave-not-in-lobby": async (t) => { await create(t); await leave(t, P2); },

  // Lobby codes
  "lobby-code": async (t) => {
    await create(t);
    const code = (await t.ssc("lobby code", "lobby_code", P1, { LobbyId: t.lobby }))?.body?.body?.LobbyCode;
    await t.ssc("lobby code by 2", "lobby_code", P2, { LobbyId: t.lobby });
    if (typeof code === "string") {
      await t.get("matches by code", `/matches/${code}`, P2);
      await t.get("matches by lowercase code", `/matches/${code.toLowerCase()}`, P2);
    }
    await t.get("matches by unknown code", "/matches/ZZZZZ", P2);
  },

  // The shared routes' custom lobby side
  "invite": async (t) => { await create(t); await t.ssc("invite", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P2, LobbyId: t.lobby, IsSpectator: true }); await t.ssc("invite by MatchID", "invite_to_player_lobby", P1, { ...COMMON, InviteeAccountID: P3, MatchID: t.lobby }); },
  "lock-loadout": async (t) => { await create(t); await join(t, P2); await t.ssc("lock 2", "lock_lobby_loadout", P2, { ...COMMON, Loadout: { Character: "character_taz", Skin: "skin_taz_default" }, LobbyId: t.lobby }); },
  // Not a bug report, a hypothesis: the TS server rewrites the whole lobby outside any script on a loadout lock and a
  // lobby code, so a change made in between (a ready) can be lost. Measured, not compared exactly.
  "race-lock-and-ready": async (t) => {
    await create(t); await join(t, P2);
    let lost = 0;
    for (let i = 0; i < 20; i++) {
      await t.redis.set(`custom_lobby_ssc:${t.lobby}`, JSON.stringify({ ...(await lobbyJson(t)), ReadyPlayers: { [P1]: true } }), { EX: 172800 });
      await Promise.all([
        request(t.baseUrl, "PUT", "/ssc/invoke/lock_lobby_loadout", P2, { ...COMMON, Loadout: { Character: "character_taz", Skin: "skin_taz_default" }, LobbyId: t.lobby }),
        request(t.baseUrl, "PUT", "/ssc/invoke/set_ready_for_lobby", P2, { ...COMMON, LobbyId: t.lobby, MatchID: t.lobby, Ready: true }),
      ]);
      if (!(await lobbyJson(t)).ReadyPlayers?.[P2]) lost++;
    }
    await t.probe("ready changes lost of 20", lost);
  },

  // Starting
  "start-with-bot": async (t) => { await create(t); await style(t, "Solos"); await bot(t, 1, 1); await oneMap(t); await start(t); },
  "start-duos-spectator-bots": async (t) => {
    await create(t); await join(t, P2); await join(t, P3, true);
    await bot(t, 1, 0); await bot(t, 2, 1, { BotSettingSlug: "VeryEasy" }); await bot(t, 3, 1, { BotSettingSlug: "Unknown" });
    await t.ssc("handicap own", "set_player_handicap_for_custom_game", P2, { MatchID: t.lobby, PlayerHandicap: 2, PlayerId: P2 });
    await oneMap(t, "M003_V5"); await start(t);
  },
  "start-buffs": async (t) => { await create(t); await join(t, P2); await mode(t, "gm_infinitejumps"); await t.ssc("world buffs", "set_world_buffs_for_custom_game", P1, { MatchID: t.lobby, WorldBuffSlugs: ["buff_low_gravity"] }); await oneMap(t, "M001"); await start(t); },
  "start-loadouts": async (t) => {
    await create(t); await join(t, P2); await join(t, P3);
    await t.ssc("lock 2", "lock_lobby_loadout", P2, { ...COMMON, Loadout: { Character: "character_taz", Skin: "skin_taz_default" }, LobbyId: t.lobby });
    await t.redis.hDel(`player:${P3}`, ["character", "skin"]); await t.redis.hDel(`connections:${P3}`, ["character", "skin"]);
    await t.redis.hSet(`connections:${P1}`, "GameplayPreferences", "0");
    await oneMap(t); await start(t);
  },
  "start-by-member": async (t) => { await create(t); await join(t, P2); await start(t, P2); },
  "start-no-maps-selected": async (t) => { await create(t); await bot(t, 1, 1); await t.ssc("no maps", "set_enabled_maps_for_custom_game", P1, { MapSlugs: [], MatchID: t.lobby }); await start(t); },

  // Every game mode: the settings, maps and buffs each one gives (the generated game data against the TS literals).
  // Two players and a spectator: the TS script leaves a lobby of more than two players as it is for a Solos mode.
  "every-game-mode": async (t) => {
    await create(t); await join(t, P2); await join(t, P3, true);
    if (process.env.SKIP_MODES) return;
    const modes = Object.keys(JSON.parse(fs.readFileSync("dotnet/src/OpenVersus.Server.Core/CustomLobbies/game-modes.json", "utf8")));
    for (const slug of modes) await mode(t, slug);
  },
};

async function run(baseUrl, outFile, filter) {
  const { redis, db, close } = await openScratch("custom_lobby_diff");
  let recording = null;
  const monitor = await openMonitor(need("REF_REDIS_URL"), (line) => recording?.push(line));
  const self = (await redis.sendCommand(["CLIENT", "INFO"])).match(/\baddr=(\S+)/)[1];

  recording = [];
  const games = await connectPlayers(need("REF_WS_URL"), PLAYERS.map((id) => ({ id, token: token(id) })));
  await sleep(200);
  const wsAddr = recording.find((l) => /"sadd" "online_players"/i.test(l))?.match(/\[\d+ ([^\]]+)\]/)?.[1];
  recording = null;
  if (!wsAddr) throw new Error("could not tell the websocket's Redis connection apart");

  const everyone = async () => {
    for (const [i, pid] of PLAYERS.entries()) {
      const n = i + 1;
      await redis.hSet(`connections:${pid}`, {
        id: pid, hydraUsername: `OpenVersus_${n}`, username: `Player${n}`, wb_network_id: pid, GameplayPreferences: String(440 + n),
        character: `character_c${n}`, skin: `skin_c${n}_default`, current_ip: IP, clientVersion: "2026.09.28.4", identityRegistered: "1",
      });
      await redis.hSet(`player:${pid}`, { character: `character_l${n}`, skin: `skin_l${n}_default` });
    }
    await db.collection("playertesters").insertMany(PLAYERS.map((pid, i) => ({
      _id: new ObjectId(pid), name: `Player${i + 1}`, character: `character_m${i + 1}`, variant: `skin_m${i + 1}_default`,
      profile_icon: `profile_icon_${i + 1}`, GameplayPreferences: 440 + i + 1, __v: 0,
    })));
    await redis.sAdd("online_players", PLAYERS);
  };

  const scenarios = [];
  for (const [name, body] of Object.entries(SCENARIOS)) {
    if (filter && !name.includes(filter)) continue;
    await redis.flushDb();
    await redis.set("refdiff:scratch", "1");
    await db.dropDatabase();
    await everyone();
    const ids = new Map(), codes = new Map();
    const steps = [];
    const scenarioStarted = Date.now();
    const t = {
      baseUrl, redis, db, lobby: null,
      async ssc(label, route, pid, body) { return record(label, "PUT", `/ssc/invoke/${route}`, pid, body); },
      async get(label, path, pid) { return record(label, "GET", path, pid); },
      async probe(label, value) { steps.push({ label, probe: value }); },
    };
    async function record(label, method, path, pid, body) {
      games.clear();
      recording = [];
      const started = Date.now();
      const answer = await request(baseUrl, method, path, pid, body);
      await sleep(400);
      const lines = recording.filter((l) => l.match(/\[\d+ ([^\]]+)\]/)?.[1] !== wsAddr).map(canonLine);
      recording = null;
      const all = writes(lines, self);
      const made = answer.body?.body?.lobby?.MatchID;
      if (/create_custom_game_lobby/.test(path) && made) t.lobby = made;
      steps.push(normalize({
        label, request: `${method} ${path}`, pid, status: answer.status, answer: answer.body,
        writes: all.filter((w) => !w.startsWith("publish ") || STATEFUL.has(w.split(" ")[1])),
        published: all.filter((w) => w.startsWith("publish ")).map((w) => w.split(" ")[1]),
        frames: games.all(),
        state: Object.fromEntries(Object.entries(await state(redis)).filter(([k]) => !/^active_ip_accounts:|^player_heartbeats$/.test(k))
          .map(([k, v]) => [k, typeof v.value === "string" && /^[[{]/.test(v.value) ? { ...v, value: (k.startsWith("custom_lobby_ssc:") ? stored : sorted)(parseJson(v.value)) } : v])),
      }, scenarioStarted, ids, codes));
      return answer;
    }
    try {
      await body(t);
    } catch (e) {
      steps.push({ label: "<harness error>", error: String(e.stack ?? e) });
    }
    // A player the client update gate refused (no session) is shown the update banner and dropped 10 s later, their
    // keys deleted: let that happen here, not in a later scenario, and connect them again.
    if (steps.some((s) => Object.values(s.frames ?? {}).flat().some((f) => f?.data?.template_id === "ToastReceivedNotification"))) {
      await sleep(10500);
      const back = await games.reopen();
      if (back.length) console.log(`  (reconnected ${back.join(", ")} after the update banner)`);
    }
    scenarios.push({ name, steps });
    process.stdout.write(`${name}: ${steps.map((s) => s.status ?? (s.probe !== undefined ? `probe ${s.probe}` : "error")).join(" ")}\n`);
  }

  games.close();
  monitor.destroy();
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, scenarios }, null, 1));
  console.log(`${scenarios.length} scenarios -> ${outFile}`);
  await close();
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function parseJson(s) {
  try { return JSON.parse(s); } catch { return s; }
}

// Objects with their keys sorted, all the way down: cjson's key order is not the lobby's.
function sorted(v) {
  if (Array.isArray(v)) return v.map(sorted);
  if (v && typeof v === "object") return Object.fromEntries(Object.keys(v).sort().map((k) => [k, sorted(v[k])]));
  return v;
}

// Stored JSON, compared: keys sorted, an empty array written as {} (cjson's way).
function stored(v) {
  if (Array.isArray(v)) return v.length ? v.map(stored) : {};
  if (v && typeof v === "object") return Object.fromEntries(Object.keys(v).sort().map((k) => [k, stored(v[k])]));
  return v;
}

// A MONITOR line with each quoted argument that is JSON rewritten with its keys sorted (and escaped again); the
// lobby's own JSON (SET custom_lobby_ssc:...) as stored JSON. A published message is read as it is (the TS websocket
// takes it verbatim): an empty array there stays one.
function canonLine(line) {
  const lobbyWrite = /"(set|setex|psetex)" "custom_lobby_ssc:/i.test(line);
  return line.replace(/"((?:[^"\\]|\\.)*)"/g, (whole, inner) => {
    if (!/^[[{]/.test(inner)) return whole;
    const text = inner.replace(/\\x([0-9a-f]{2})/gi, (_, h) => String.fromCharCode(parseInt(h, 16))).replace(/\\(.)/g, "$1");
    try {
      return `"${JSON.stringify((lobbyWrite ? stored : sorted)(JSON.parse(text))).replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
    } catch {
      return whole;
    }
  });
}

// What is new on every request, once its format is checked: fresh ids (the harness's own start 0000; bots' are
// Bot0...) renamed in order of first appearance within the scenario, times from a minute before the scenario to a
// minute from now, lobby codes, match keys, rollback ports and Math.random. A value that fails its format check is left
// as it is (and so differs).
function normalize(step, started, ids, codes) {
  const nearMs = (n) => n > started - 60000 && n < Date.now() + 60000;
  const nearSeconds = (n) => nearMs(n * 1000);
  // A lobby code is learnt where its format is certain (its key lobby_code:{code}, the LobbyCode answer), then
  // replaced wherever else it shows.
  const learn = (c) => codes.has(c) || codes.set(c, `<code ${codes.size + 1}>`);
  for (const w of step.writes ?? []) for (const m of w.matchAll(/lobby_code:([A-HJ-NP-Z2-9]{5})\b/g)) learn(m[1]);
  const answered = step.answer?.body?.LobbyCode;
  if (typeof answered === "string" && CODE.test(answered)) learn(answered);
  const rename = (s) => {
    s = s.replace(/\b[0-9a-f]{24}\b/g, (id) => (id.startsWith("0000") ? id : (ids.has(id) || ids.set(id, `<new id ${ids.size + 1}>`), ids.get(id))))
      .replace(/\b1\d{12}\b/g, (n) => (nearMs(Number(n)) ? "<now ms>" : n))
      .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z/g, (d) => (nearMs(Date.parse(d)) ? "<now iso>" : d))
      .replace(/\\"matchKey\\":\\"([A-Za-z0-9+/=]{44})\\"/g, '\\"matchKey\\":\\"<match key>\\"')
      .replace(/\\"rollbackPort\\":(\d{1,5})\b/g, '\\"rollbackPort\\":\\"<port>\\"');
    for (const [c, name] of codes) s = s.replace(new RegExp(`\\b${c}\\b`, "gi"), name);
    return s;
  };
  const walk = (v, key) => {
    if (Array.isArray(v)) return v.map((x) => walk(x, key));
    if (v && typeof v === "object") {
      if (Object.keys(v).length === 1 && typeof v._hydra_unix_date === "number" && nearSeconds(v._hydra_unix_date)) return "<now>";
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [rename(k), walk(x, k)]));
    }
    if (key === "rand" && typeof v === "number") return "<random>";
    if ((key === "matchKey" || key === "MatchKey") && typeof v === "string" && Buffer.from(v, "base64").length === 32) return "<match key>";
    if ((key === "rollbackPort" || key === "Port") && Number.isInteger(v) && v > 0 && v < 65536) return "<port>";
    if (typeof v === "number" && nearMs(v)) return "<now ms>";
    if (typeof v === "string") return rename(v);
    return v;
  };
  return walk(step);
}

// The deliberate differences: for a scenario's step (by label), what the difference must be; any other difference
// is reported like any other.
const same = (x, y, ...parts) => parts.every((k) => JSON.stringify(sorted(x?.[k])) === JSON.stringify(sorted(y?.[k])));
const lobbyOf = (step) => Object.entries(step?.state ?? {}).find(([k]) => k.startsWith("custom_lobby_ssc:"))?.[1]?.value;
const teamOf = (lobby, pid) => lobby?.Teams && Object.values(lobby.Teams).filter((t) => t.Players?.[pid]).map((t) => t.TeamIndex);
const ids = (lobby) => Object.values(lobby?.Teams ?? {}).flatMap((t) => Object.keys(t.Players ?? {}));
// The players a lobby counts (its teams' Length) against those in it, each once.
const countsRight = (lobby) => Object.values(lobby?.Teams ?? {}).reduce((n, t) => n + t.Length, 0) === new Set(ids(lobby)).size;
// A lobby write done in a script (the port's): SET k v then EXPIRE k s, where the TS server wrote SET k v EX s.
const scripted = (ts, cs) => {
  const split = ts.writes.flatMap((w) => { const m = /^set (custom_lobby_ssc:(?:<new id \d+>|\S+)) (.+) EX (\d+)$/.exec(w); return m ? [`set ${m[1]} ${m[2]}`, `expire ${m[1]} ${m[3]}`] : [w]; });
  return JSON.stringify(split) === JSON.stringify(cs.writes);
};
const EXPECTED = {
  "join-twice / join 2 again": {
    why: "a player already in the lobby is answered the lobby, not added again (the TS server put them in a second team)",
    holds: (ts, cs) => teamOf(lobbyOf(ts), P2).length === 2 && teamOf(lobbyOf(cs), P2).length === 1 && ids(lobbyOf(cs)).length === 2
      && cs.status === 200 && cs.answer?.return_code === 0 && Object.values(cs.frames).every((f) => f.length === 0),
  },
  "join-twice / ready 2": {
    why: "(after the double join) everyone ready: all ready in the port's lobby; never in the TS server's (P2 counted twice)",
    holds: (ts, cs) => ts.answer?.body?.bAllPlayersReady === false && cs.answer?.body?.bAllPlayersReady === true,
  },
  "join-twice / ready 1": {
    why: "(after the double join) everyone ready: all ready in the port's lobby; never in the TS server's (P2 counted twice)",
    holds: (ts, cs) => ts.answer?.body?.bAllPlayersReady === false && cs.answer?.body?.bAllPlayersReady === true,
  },
  "join-own-lobby / join 1": {
    why: "the leader joining their own lobby is answered it; the TS server added them over themselves (Length 2, one player)",
    holds: (ts, cs) => !countsRight(lobbyOf(ts)) && countsRight(lobbyOf(cs)) && cs.answer?.return_code === 0,
  },
  "rejoin-after-start / join 2 after the match": {
    why: "a player rejoining after the match is answered the lobby, not added again",
    holds: (ts, cs) => teamOf(lobbyOf(ts), P2).length === 2 && teamOf(lobbyOf(cs), P2).length === 1 && cs.answer?.return_code === 0,
  },
  "rejoin-after-start / ready 2": {
    why: "(after the rejoin) all ready in the port's lobby; never in the TS server's",
    holds: (ts, cs) => ts.answer?.body?.bAllPlayersReady === false && cs.answer?.body?.bAllPlayersReady === true,
  },
  "switch-ffa / switch 2 to 2": {
    why: "a player moves to an empty FFA slot; the TS server allowed team changes only in Duos ({} answered)",
    holds: (ts, cs) => JSON.stringify(ts.answer) === "{}" && cs.answer?.body?.TeamIndex === 2 && teamOf(lobbyOf(cs), P2)[0] === 2,
  },
  "promote / promote by 3": {
    why: "only the leader promotes: refused (the TS server let any member make themselves leader)",
    holds: (ts, cs) => lobbyOf(ts)?.LeaderID === P3 && lobbyOf(cs)?.LeaderID === P2 && cs.answer?.return_code === 1
      && Object.values(cs.frames).every((f) => f.length === 0),
  },
  "promote / promote absent": {
    why: "(after the refused promote) the same refusal, P2 still leading",
    holds: (ts, cs) => same(ts, cs, "answer", "writes", "frames") && lobbyOf(cs)?.LeaderID === P2,
  },
  "leave-leader-earliest-on-later-team / leave 1": {
    why: "the lead passes to the player who joined first (P2, team 1); the TS server took the first of the first team (P3)",
    holds: (ts, cs) => lobbyOf(ts)?.LeaderID === P3 && lobbyOf(cs)?.LeaderID === P2 && same(ts, cs, "answer"),
  },
  "leave-leader-earliest-spectating / leave 1": {
    why: "the lead passes to the player who joined first (P2, spectating); the TS server took the first of the first team (P3)",
    holds: (ts, cs) => lobbyOf(ts)?.LeaderID === P3 && lobbyOf(cs)?.LeaderID === P2 && same(ts, cs, "answer"),
  },
  "leave-leader-bot-before-human / leave 1": {
    why: "the lead passes to a player, never a bot; the TS server made the bot leader",
    holds: (ts, cs) => lobbyOf(ts)?.LeaderID === BOT(1) && lobbyOf(cs)?.LeaderID === P2 && same(ts, cs, "answer"),
  },
  "race-lock-and-ready / ready changes lost of 20": {
    why: "a loadout lock no longer loses a ready made at the same time (a script); the TS server lost some",
    holds: (ts, cs) => ts.probe > 0 && cs.probe === 0,
  },
  "lobby-code / matches by unknown code": {
    why: "not a lobby code: the port answers as its stubs do (not ported); the TS server's catch-all answered",
    holds: (ts, cs) => ts.status === 200 && cs.status === 501 && same(ts, cs, "writes", "frames", "state"),
  },
};
// The lobby writes the port does in a script (lobby code, loadout lock): the same JSON, as SET then EXPIRE.
for (const label of ["leave-last-with-code / lobby code", "lobby-code / lobby code", "lock-loadout / lock 2", "start-loadouts / lock 2"]) {
  EXPECTED[label] = {
    why: "the lobby written by a script (SET, EXPIRE) where the TS server wrote SET EX",
    holds: (ts, cs) => scripted(ts, cs) && same(ts, cs, "status", "answer", "frames", "state"),
  };
}

function diffRuns(fileA, fileB) {
  const a = JSON.parse(fs.readFileSync(fileA, "utf8")), b = JSON.parse(fs.readFileSync(fileB, "utf8"));
  const show = (v) => JSON.stringify(sorted(v));
  let differing = 0, steps = 0;
  const names = [...new Set([...a.scenarios.map((s) => s.name), ...b.scenarios.map((s) => s.name)])];
  for (const name of names) {
    const x = a.scenarios.find((s) => s.name === name), y = b.scenarios.find((s) => s.name === name);
    const count = Math.max(x?.steps.length ?? 0, y?.steps.length ?? 0);
    for (let i = 0; i < count; i++) {
      steps++;
      const p = x?.steps[i], q = y?.steps[i];
      // A probe counts lost changes: what is compared is whether there were any. A TTL over two hours is compared to
      // the hour: a long scenario (every game mode, at the TS server's 1.5 s each) ages a 2-day key past a minute.
      const hours = (state) => state && Object.fromEntries(Object.entries(state).map(([k, v]) => [k, typeof v?.ttl === "string" && /^~\d+m$/.test(v.ttl) && parseInt(v.ttl.slice(1)) >= 120 ? { ...v, ttl: `~${Math.round(parseInt(v.ttl.slice(1)) / 60)}h` } : v]));
      const lossy = (step) => (step ? { ...step, state: hours(step.state), ...("probe" in step ? { probe: step.probe > 0 } : {}) } : step);
      const parts = ["label", "status", "answer", "writes", "frames", "state", "probe", "error"].filter((k) => show(lossy(p)?.[k]) !== show(lossy(q)?.[k]));
      const label = `${name} / ${i + 1} ${p?.label ?? q?.label}`;
      const expected = EXPECTED[`${name} / ${p?.label}`];
      if (!parts.length) {
        // A deliberate difference that is not there is a fix that went missing.
        if (expected && !expected.optional) {
          differing++;
          console.log(`${label}: the same on both, but a difference is expected (${expected.why})`);
        }
        continue;
      }
      if (expected?.holds(p, q)) {
        console.log(`${label}: differs in ${parts.join(", ")} (expected: ${expected.why})`);
        continue;
      }
      if (expected) console.log(`${label}: NOT the expected difference (${expected.why})`);
      differing++;
      console.log(`${label}: differs in ${parts.join(", ")}`);
      for (const k of parts) {
        if (k === "writes") {
          const only = (from, other) => from.filter((w, j) => from.slice(0, j).filter((v) => v === w).length >= other.filter((v) => v === w).length);
          console.log(`  writes only A: ${JSON.stringify(only(p?.writes ?? [], q?.writes ?? [])).slice(0, 3000)}`);
          console.log(`  writes only B: ${JSON.stringify(only(q?.writes ?? [], p?.writes ?? [])).slice(0, 3000)}`);
          continue;
        }
        if (k === "state" || k === "frames") {
          const keys = [...new Set([...Object.keys(p?.[k] ?? {}), ...Object.keys(q?.[k] ?? {})])].filter((key) => show(p?.[k]?.[key]) !== show(q?.[k]?.[key]));
          for (const key of keys) {
            console.log(`  ${k}.${key} A: ${String(show(p?.[k]?.[key])).slice(0, 1500)}`);
            console.log(`  ${k}.${key} B: ${String(show(q?.[k]?.[key])).slice(0, 1500)}`);
          }
          continue;
        }
        console.log(`  ${k} A: ${String(show(p?.[k])).slice(0, 2000)}`);
        console.log(`  ${k} B: ${String(show(q?.[k])).slice(0, 2000)}`);
      }
    }
  }
  console.log(differing ? `${differing} of ${steps} step(s) differ` : `no differences in ${steps} steps`);
  process.exit(differing ? 1 : 0);
}

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1], args[2]);
else if (command === "diff") diffRuns(args[0], args[1]);
else {
  console.error("usage: custom_lobby_diff.mjs run <baseUrl> <out.json> [filter] | diff <a.json> <b.json>");
  process.exit(2);
}
