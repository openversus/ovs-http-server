import ObjectID from "bson-objectid";
import { randomBytes, randomInt } from "crypto";
import {
  redisClient,
  redisUpdateMatch,
  redisOnGameplayConfigNotified,
  redisMatchMakingComplete,
  redisLockPerks,
  type RedisMatch,
  type RedisMatchTicket,
  type RedisTeamEntry,
  type MATCH_FOUND_NOTIFICATION,
} from "../../config/redis";
import { logger, logwrapper } from "../../config/logger";
import env from "../../env/env";
import { GAME_MODES_CONFIG } from "./gameModes.data";
import { MAP_ROTATIONS } from "./maps.data";
import { BOT_DEFAULT_PERKS, BOT_DIFFICULTY } from "../../data/botDefaults";
import {
  TeamStyle,
  MATCH_TYPES,
  CUSTOM_LOBBY_NOTIFICATION_CHANNEL,
  LOBBY_JOINED_CHANNEL,
  type BaseLobby,
  type CustomLobby,
  type CustomLobbyMatchConfig,
  type CustomLobbySettings,
  type CustomLobbyNotificationMessage,
  type LobbyTeam,
  type NotificationTemplate,
  type PartyLobby,
  type PlayerConfig,
} from "./lobby.types";
import { IDeployInfo, DeployInfo, getDefaultDeployInfo } from "../../services/rollbackService";

const logPrefix = "[CustomLobby.Service]:";
const LOBBY_EX = 2 * 24 * 60 * 60; // 2 days
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

const useOnDemandRollback: boolean = env.ON_DEMAND_ROLLBACK === 1;
let customLobbyUDPPortLow: number = useOnDemandRollback ? env.ON_DEMAND_ROLLBACK_PORT_LOW : env.ROLLBACK_UDP_PORT_LOW
let customLobbyUDPPortHigh: number = useOnDemandRollback ? env.ON_DEMAND_ROLLBACK_PORT_HIGH : env.ROLLBACK_UDP_PORT_HIGH

function lobbyKey(lobbyId: string) {
  return `custom_lobby_ssc:${lobbyId}`;
}

function generateCode(len = 5): string {
  let out = "";
  for (let i = 0; i < len; i++) {
    out += ALPHABET[(Math.random() * ALPHABET.length) | 0];
  }
  return out;
}

function sleep(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// ─── Lua cjson fix ───────────────────────────────────────────────────────────
// Redis cjson.encode({}) can produce [] instead of {} for empty Lua tables.
// The game parser crashes on Players:[] (expects object). Fix in Node after parse.
// Fields that must be objects (maps): cjson may encode empty {} as []
const OBJECT_FIELDS = new Set([
  "Players", "ReadyPlayers", "PlayerGameplayPreferences",
  "PlayerAutoPartyPreferences", "Platforms", "LockedLoadouts",
  "Handicaps", "PlayerBuffs", "AllMultiplayParams", "Spectators",
  "CustomGameSettings", "HudSettings",
]);

// Fields that must be arrays: cjson may encode empty [] as {}
const ARRAY_FIELDS = new Set([
  "WorldBuffs", "Maps", "Teams", "Taunts", "Perks", "Buffs",
  "StatTrackers", "Gems", "Bundles", "TeamData",
  "MapsInRotation", "RequiredWorldBuffs", "RequiredTeamPlayerBuffs",
  "RequiredPlayerBuffs",
]);

function fixCjsonEmptyTables(obj: any): any {
  if (obj === null || obj === undefined || typeof obj !== "object") return obj;
  if (Array.isArray(obj)) return obj.map(fixCjsonEmptyTables);
  for (const key of Object.keys(obj)) {
    const val = obj[key];
    if (val && typeof val === "object") {
      const isEmpty = Array.isArray(val) ? val.length === 0 :
        Object.keys(val).length === 0;
      if (isEmpty) {
        if (OBJECT_FIELDS.has(key)) {
          obj[key] = {};
        } else if (ARRAY_FIELDS.has(key)) {
          obj[key] = [];
        }
        // else leave as-is
      } else {
        obj[key] = fixCjsonEmptyTables(val);
      }
    }
  }
  return obj;
}

function parseLuaResult<T>(raw: string | null): T | null {
  if (!raw) return null;
  try {
    return fixCjsonEmptyTables(JSON.parse(raw)) as T;
  } catch {
    return null;
  }
}

// ─── Lua scripts (converted from Redis JSON to standard Redis GET/SET) ───────
// All scripts: GET → cjson.decode, modify table, cjson.encode → SET + EXPIRE

const LUA_SET_LOBBY_JOINABLE = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
l.IsLobbyJoinable = (ARGV[2] == 'true')
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return 1
`;

const LUA_SET_LOBBY_MODE = `
local s = redis.call('GET', KEYS[1])
if not s then return redis.error_reply('Lobby not found') end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return redis.error_reply('Not the leader') end
l.ModeString = ARGV[2]
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return 1
`;

const LUA_LOCK_LOBBY = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID == ARGV[1] then return nil end
l.IsLobbyJoinable = false
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return 1
`;

const LUA_APPLY_GAME_SETTINGS = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end

local newConfig = cjson.decode(ARGV[3])
local newStyle  = newConfig.TeamStyle
local oldStyle  = l.match_config.TeamStyle

if oldStyle ~= newStyle then
  local players = {}
  for _, team in ipairs(l.Teams) do
    if team.TeamIndex ~= 4 then
      for pid, pdata in pairs(team.Players) do
        table.insert(players, { pid = pid, pdata = pdata })
      end
    end
  end
  local total = #players

  if newStyle == 'Solos' and total > 2 then
    return cjson.encode(l)
  end

  local teamByIdx = {}
  for i, team in ipairs(l.Teams) do
    teamByIdx[team.TeamIndex] = i
  end

  for _, team in ipairs(l.Teams) do
    if team.TeamIndex ~= 4 then
      team.Players = {}
      team.Length   = 0
    end
  end

  for i, player in ipairs(players) do
    local targetTeamIdx
    if newStyle == 'FFA' then
      targetTeamIdx = i - 1
    elseif newStyle == 'Duos' then
      targetTeamIdx = math.floor((i - 1) / 2)
    elseif newStyle == 'Solos' then
      targetTeamIdx = i - 1
    else
      targetTeamIdx = 0
    end
    local arrIdx = teamByIdx[targetTeamIdx]
    if arrIdx then
      l.Teams[arrIdx].Players[player.pid] = player.pdata
      l.Teams[arrIdx].Length = l.Teams[arrIdx].Length + 1
    end
  end
end

l.GameModeSlug = ARGV[2]
l.match_config = cjson.decode(ARGV[3])
l.Maps         = cjson.decode(ARGV[4])
l.WorldBuffs   = cjson.decode(ARGV[5])

local matrix = cjson.decode(ARGV[6])
local newBuffs = {}
for _, team in ipairs(l.Teams) do
  local teamKey = tostring(team.TeamIndex)
  local teamData = matrix[teamKey]
  if teamData then
    local teamBuffs = teamData.teamBuffs or {}
    local playerBuffsMap = teamData.players or {}
    local slotIdx = 0
    for pid, _ in pairs(team.Players) do
      local playerSpecificBuffs = playerBuffsMap[tostring(slotIdx)] or {}
      local combined = {}
      for _, b in ipairs(playerSpecificBuffs) do table.insert(combined, b) end
      for _, b in ipairs(teamBuffs) do table.insert(combined, b) end
      if #combined > 0 then newBuffs[pid] = combined end
      slotIdx = slotIdx + 1
    end
  end
end
l.PlayerBuffs = newBuffs
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l)
`;

const LUA_UPDATE_INT_SETTING = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
l.match_config[ARGV[2]] = cjson.decode(ARGV[3])
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l)
`;

const LUA_UPDATE_ENABLED_MAPS = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local enabled = {}
for i = 2, #ARGV do enabled[ARGV[i]] = true end
for i, m in ipairs(l.Maps) do
  l.Maps[i].IsSelected = enabled[m.Map] ~= nil
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l.Maps)
`;

const LUA_SET_WORLD_BUFFS = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
l.WorldBuffs = cjson.decode(ARGV[2])
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return 1
`;

const LUA_UPDATE_HANDICAP = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
l.Handicaps[ARGV[1]] = tonumber(ARGV[2])
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l)
`;

const LUA_SWITCH_TEAM = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)

local pid = ARGV[1]
local targetIdx = tonumber(ARGV[2])
local style = l.match_config.TeamStyle

local fromArrIdx = nil
local fromTeamIdx = nil
local pdata = nil
for i, team in ipairs(l.Teams) do
  if team.Players[pid] then
    fromArrIdx = i
    fromTeamIdx = team.TeamIndex
    pdata = team.Players[pid]
    break
  end
end
if fromArrIdx == nil then return nil end

local targetArrIdx = nil
local targetLen = nil
for i, team in ipairs(l.Teams) do
  if team.TeamIndex == targetIdx then
    targetArrIdx = i
    targetLen = team.Length
    break
  end
end
if targetArrIdx == nil then return nil end

if fromArrIdx == targetArrIdx then
  return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
end

if targetIdx == 4 then
  if targetLen >= 4 then return nil end
elseif fromTeamIdx == 4 then
  if style == 'Duos' then
    if targetIdx ~= 0 and targetIdx ~= 1 then return nil end
    if targetLen >= 2 then return nil end
  elseif style == 'FFA' then
    if targetIdx < 0 or targetIdx > 3 then return nil end
    if targetLen >= 1 then return nil end
  else
    if targetIdx ~= 0 and targetIdx ~= 1 then return nil end
    if targetLen >= 1 then return nil end
  end
else
  if style ~= 'Duos' then return nil end
  if targetIdx ~= 0 and targetIdx ~= 1 then return nil end
  if targetLen >= 2 then return nil end
end

l.Teams[fromArrIdx].Players[pid] = nil
l.Teams[fromArrIdx].Length = l.Teams[fromArrIdx].Length - 1
l.Teams[targetArrIdx].Players[pid] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1

redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
`;

const LUA_JOIN_CUSTOM_LOBBY = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.IsLobbyJoinable == false then return cjson.encode(false) end

local accountId   = ARGV[1]
local isSpectator = ARGV[2] == 'true'
local ts          = ARGV[3]
local loadout     = ARGV[4]
local style       = l.match_config.TeamStyle

local totalPlayers = 0
for _, team in ipairs(l.Teams) do
  totalPlayers = totalPlayers + team.Length
end

local targetArrIdx = nil
for i, team in ipairs(l.Teams) do
  local ok = false
  if isSpectator then
    ok = team.TeamIndex == 4 and team.Length < 4
  elseif style == 'FFA' then
    ok = team.TeamIndex ~= 4 and team.Length < 1
  elseif style == 'Solos' or style == 'Other' then
    ok = (team.TeamIndex == 0 or team.TeamIndex == 1) and team.Length < 1
  elseif style == 'Duos' then
    ok = (team.TeamIndex == 0 or team.TeamIndex == 1) and team.Length < 2
  end
  if ok then
    targetArrIdx = i
    break
  end
end
if targetArrIdx == nil then
  if not isSpectator then
    for i, team in ipairs(l.Teams) do
      if team.TeamIndex == 4 and team.Length < 4 then
        targetArrIdx = i
        isSpectator = true
        break
      end
    end
    if targetArrIdx == nil then return nil end
  end
end

local pdata = {
  Account           = { id = accountId },
  JoinedAt          = ts,
  BotSettingSlug    = '',
  LobbyPlayerIndex  = totalPlayers,
  CrossplayPreference = 1
}

l.Teams[targetArrIdx].Players[accountId] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1
l.PlayerAutoPartyPreferences[accountId] = false
l.PlayerGameplayPreferences[accountId] = 964
l.Platforms[accountId] = 'PC'
if loadout ~= '' then
  l.LockedLoadouts[accountId] = cjson.decode(loadout)
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l)
`;

const LUA_SET_PLAYER_READY = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local pid = ARGV[1]
local ready = ARGV[2] == 'true'
if ready then
  l.ReadyPlayers[pid] = true
else
  l.ReadyPlayers[pid] = nil
end
local readyCount = 0
for _ in pairs(l.ReadyPlayers) do readyCount = readyCount + 1 end
local totalPlayers = 0
for _, team in ipairs(l.Teams) do
  if team.TeamIndex ~= 4 then totalPlayers = totalPlayers + team.Length end
end
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return (readyCount >= totalPlayers and totalPlayers > 0) and 1 or 0
`;

const LUA_ADD_CUSTOM_GAME_BOT = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local botId     = ARGV[2]
local targetIdx = tonumber(ARGV[3])
local targetArrIdx = nil
local globalPlayerCount = 0
for i, team in ipairs(l.Teams) do
  globalPlayerCount = globalPlayerCount + team.Length
  if team.TeamIndex == targetIdx then
    targetArrIdx = i
  end
end
if targetArrIdx == nil then return nil end
local pdata = cjson.decode(ARGV[4])
pdata.LobbyPlayerIndex = globalPlayerCount
l.Teams[targetArrIdx].Players[botId] = pdata
l.Teams[targetArrIdx].Length = l.Teams[targetArrIdx].Length + 1
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
`;

const LUA_UPDATE_BOT_FIGHTER = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return nil end
local botId = ARGV[2]
local teamArrIdx = nil
for i, team in ipairs(l.Teams) do
  if team.Players[botId] then
    teamArrIdx = i
    break
  end
end
if teamArrIdx == nil then return nil end
l.Teams[teamArrIdx].Players[botId].Fighter       = cjson.decode(ARGV[3])
l.Teams[teamArrIdx].Players[botId].Skin           = cjson.decode(ARGV[4])
l.Teams[teamArrIdx].Players[botId].BotSettingSlug = ARGV[5]
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l.Teams[teamArrIdx].Players[botId])
`;

const LUA_KICK_FROM_LOBBY = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.LeaderID ~= ARGV[1] then return redis.error_reply('Not the leader') end
local pid = ARGV[2]
if pid == ARGV[1] then return redis.error_reply('Cannot kick yourself') end

local fromArrIdx = nil
local pdata = nil
for i, team in ipairs(l.Teams) do
  if team.Players[pid] then
    fromArrIdx = i
    pdata = team.Players[pid]
    break
  end
end
if fromArrIdx == nil then return nil end

l.Teams[fromArrIdx].Players[pid] = nil
l.Teams[fromArrIdx].Length = l.Teams[fromArrIdx].Length - 1
l.ReadyPlayers[pid] = nil
l.PlayerAutoPartyPreferences[pid] = nil
l.PlayerGameplayPreferences[pid] = nil
l.Platforms[pid] = nil
l.LockedLoadouts[pid] = nil
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode({ playerData = pdata, gameModeSlug = l.GameModeSlug })
`;

const LUA_PROMOTE_LEADER = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local target = ARGV[1]
local found = false
for _, team in ipairs(l.Teams) do
  if team.Players[target] then found = true; break end
end
if not found then return nil end
l.LeaderID = target
l.ReadyPlayers[target] = true
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return cjson.encode(l)
`;

const LUA_LEAVE_CUSTOM_LOBBY = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local pid = ARGV[1]

local fromArrIdx = nil
local pdata = nil
for i, team in ipairs(l.Teams) do
  if team.Players[pid] then
    fromArrIdx = i
    pdata = team.Players[pid]
    break
  end
end
if fromArrIdx == nil then return nil end

l.Teams[fromArrIdx].Players[pid] = nil
l.Teams[fromArrIdx].Length = l.Teams[fromArrIdx].Length - 1
l.ReadyPlayers[pid] = nil
l.PlayerAutoPartyPreferences[pid] = nil
l.PlayerGameplayPreferences[pid] = nil
l.Platforms[pid] = nil
l.LockedLoadouts[pid] = nil

local newLeader = nil
if l.LeaderID == pid then
  for i, team in ipairs(l.Teams) do
    for otherId, _ in pairs(team.Players) do
      if otherId ~= pid then newLeader = otherId; break end
    end
    if newLeader then break end
  end
  if newLeader then
    l.LeaderID = newLeader
    l.ReadyPlayers[newLeader] = true
  else
    if l.LobbyCode and l.LobbyCode ~= '' then
      redis.call('DEL', 'lobby_code:' .. l.LobbyCode)
    end
    redis.call('DEL', KEYS[1])
    return cjson.encode({ playerData = pdata, readyPlayers = {}, leaderID = pid, gameModeSlug = l.GameModeSlug })
  end
end

redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
local leaderID = newLeader or l.LeaderID
return cjson.encode({ playerData = pdata, readyPlayers = l.ReadyPlayers, leaderID = leaderID, gameModeSlug = l.GameModeSlug })
`;

const LUA_REFRESH_PLAYER_BUFFS = `
local s = redis.call('GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
local matrix = cjson.decode(ARGV[1])
local newBuffs = {}
for _, team in ipairs(l.Teams) do
  local teamKey = tostring(team.TeamIndex)
  local teamData = matrix[teamKey]
  if teamData then
    local teamBuffs = teamData.teamBuffs or {}
    local playerBuffsMap = teamData.players or {}
    local slotIdx = 0
    for pid, _ in pairs(team.Players) do
      local playerSpecificBuffs = playerBuffsMap[tostring(slotIdx)] or {}
      local combined = {}
      for _, b in ipairs(playerSpecificBuffs) do table.insert(combined, b) end
      for _, b in ipairs(teamBuffs) do table.insert(combined, b) end
      if #combined > 0 then newBuffs[pid] = combined end
      slotIdx = slotIdx + 1
    end
  end
end
l.PlayerBuffs = newBuffs
redis.call('SET', KEYS[1], cjson.encode(l))
redis.call('EXPIRE', KEYS[1], ${LOBBY_EX})
return 1
`;

// ─── Helpers ──────────────────────────────────────────────────────────────────

function evalLua(script: string, keys: string[], args: string[]) {
  return redisClient.eval(script, { keys, arguments: args });
}

const TEAM_STYLE_TO_GAME_MODE: Partial<Record<TeamStyle, keyof typeof GAME_MODES_CONFIG>> = {
  [TeamStyle.Duos]: "gm_classic_2v2",
  [TeamStyle.Solos]: "gm_classic_1v1",
  [TeamStyle.FFA]: "gm_classic_ffa",
};

function computeBuffMatrix(gameModeSlug: keyof typeof GAME_MODES_CONFIG) {
  const gmTeams = GAME_MODES_CONFIG[gameModeSlug].data.GameModeData.GameModeTeams;
  const matrix: Record<string, { teamBuffs: string[]; players: Record<string, string[]> }> = {};
  gmTeams.forEach((gmTeam: any, teamIdx: number) => {
    const players: Record<string, string[]> = {};
    (gmTeam.Players ?? []).forEach((p: any, playerIdx: number) => {
      if (p.RequiredPlayerBuffs?.length > 0) {
        players[playerIdx.toString()] = p.RequiredPlayerBuffs;
      }
    });
    matrix[teamIdx.toString()] = {
      teamBuffs: gmTeam.RequiredTeamPlayerBuffs ?? [],
      players,
    };
  });
  return matrix;
}

async function applyGameSettings(
  lobbyId: string,
  leaderId: string,
  gameModeSlug: keyof typeof GAME_MODES_CONFIG,
) {
  const settings = getCustomLobbyDefaultSettings(gameModeSlug);
  const matrix = computeBuffMatrix(gameModeSlug);
  const result = await evalLua(
    LUA_APPLY_GAME_SETTINGS,
    [lobbyKey(lobbyId)],
    [
      leaderId,
      gameModeSlug,
      JSON.stringify(settings.match_config),
      JSON.stringify(settings.Maps),
      JSON.stringify(settings.WorldBuffs),
      JSON.stringify(matrix),
    ],
  );
  return result ? (parseLuaResult<CustomLobby>(result as string)!) : null;
}

async function refreshPlayerBuffs(lobbyId: string, gameModeSlug: keyof typeof GAME_MODES_CONFIG) {
  const matrix = computeBuffMatrix(gameModeSlug);
  await evalLua(LUA_REFRESH_PLAYER_BUFFS, [lobbyKey(lobbyId)], [JSON.stringify(matrix)]);
}

// ─── Notification Broadcasting ────────────────────────────────────────────────

async function broadcastToTopic(lobbyId: string, notification: NotificationTemplate) {
  const lobby = await getLobby(lobbyId);
  if (!lobby) return;
  const playerIds = Object.keys(lobby.PlayerGameplayPreferences);
  const msg: CustomLobbyNotificationMessage = {
    targetPlayerIds: playerIds,
    excludePlayerIds: [],
    notification,
  };
  await redisClient.publish(CUSTOM_LOBBY_NOTIFICATION_CHANNEL, JSON.stringify(msg));
}

async function broadcastToUsers(
  users: string[],
  exclude: string[],
  notification: NotificationTemplate,
) {
  const msg: CustomLobbyNotificationMessage = {
    targetPlayerIds: users,
    excludePlayerIds: exclude,
    notification,
  };
  await redisClient.publish(CUSTOM_LOBBY_NOTIFICATION_CHANNEL, JSON.stringify(msg));
}

// ─── Player Config Adapter ───────────────────────────────────────────────────
// Builds a PlayerConfig from our existing Redis data (player:{id} hash + connections:{id} hash)

async function getPlayerConfig(accountId: string): Promise<PlayerConfig | null> {
  const [playerData, connData] = await Promise.all([
    redisClient.hGetAll(`player:${accountId}`),
    redisClient.hGetAll(`connections:${accountId}`),
  ]);

  const character = playerData?.character || connData?.character || "";
  const skin = playerData?.skin || connData?.skin || "";
  const username = connData?.username || connData?.hydraUsername || "Unknown";
  const profileIcon = playerData?.profileIcon || connData?.profileIcon || "";
  const gameplayPrefs = Number(connData?.GameplayPreferences) || 964;

  return {
    AccountId: accountId,
    Username: username,
    bUseCharacterDisplayName: false,
    PlayerIndex: 0,
    TeamIndex: 0,
    Character: character,
    Skin: skin,
    Taunts: [],
    Perks: [],
    Banner: "banner_default",
    ProfileIcon: profileIcon,
    RingoutVfx: "ring_out_vfx_default",
    bIsBot: false,
    BotBehaviorOverride: "",
    BotDifficultyMin: null,
    BotDifficultyMax: null,
    Buffs: [],
    StatTrackers: [],
    Gems: [],
    StartingDamage: 0,
    Handicap: 0,
    GameplayPreferences: gameplayPrefs,
    bAutoPartyPreference: false,
    PartyMember: null,
    PartyId: null,
    RankedTier: null,
    RankedDivision: null,
    WinStreak: null,
    IsHost: false,
    Ip: connData?.current_ip || "",
  };
}

async function getPlayersConfig(playerIds: string[]): Promise<Map<string, PlayerConfig>> {
  const configs = new Map<string, PlayerConfig>();
  const results = await Promise.all(playerIds.map((id) => getPlayerConfig(id)));
  for (const config of results) {
    if (config) configs.set(config.AccountId, config);
  }
  return configs;
}

// ─── Public API ───────────────────────────────────────────────────────────────

export async function getPlayerCustomLobbyId(playerId: string): Promise<string | null> {
  return await redisClient.get(`ssc_custom_lobby_player:${playerId}`);
}

export async function getLobby(lobbyId: string): Promise<BaseLobby | null> {
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (!raw) return null;
  try {
    return fixCjsonEmptyTables(JSON.parse(raw)) as BaseLobby;
  } catch {
    return null;
  }
}

export async function setLobbyJoinable(lobbyId: string, leaderId: string, joinable: boolean) {
  await evalLua(
    LUA_SET_LOBBY_JOINABLE,
    [lobbyKey(lobbyId)],
    [leaderId, joinable ? "true" : "false"],
  );
}

export async function invitePlayerToLobby(
  MatchID: string,
  InviterAccountId: string,
  InviteeAccountId: string,
  IsSpectator: boolean,
) {
  const lobby = await getLobby(MatchID);
  if (!lobby) return;

  const notification: NotificationTemplate = {
    data: {
      LobbyType: 0,
      MatchID,
      ContextData: { LobbyType: "Custom" },
      template_id: "InviteReceivedForLobby",
      IsSpectator,
      InviterAccountId,
    },
    payload: {
      frm: { id: "internal-server", type: "server-api-key" },
      template: "realtime",
      account_id: InviteeAccountId,
      profile_id: InviteeAccountId,
    },
    header: "",
    cmd: "profile-notification",
  };

  const msg: CustomLobbyNotificationMessage = {
    targetPlayerIds: [InviteeAccountId],
    excludePlayerIds: [],
    notification,
  };
  await redisClient.publish(CUSTOM_LOBBY_NOTIFICATION_CHANNEL, JSON.stringify(msg));
}

export async function createBaseLobby(accountId: string): Promise<BaseLobby> {
  const playerConfig = await getPlayerConfig(accountId);
  const gameplayPrefs = playerConfig?.GameplayPreferences ?? 964;
  const character = playerConfig?.Character ?? "";
  const skin = playerConfig?.Skin ?? "";

  return {
    Teams: [
      {
        TeamIndex: 0,
        Players: {
          [accountId]: {
            Account: { id: accountId },
            JoinedAt: new Date().toISOString(),
            BotSettingSlug: "",
            LobbyPlayerIndex: 0,
            CrossplayPreference: 1,
          },
        },
        Length: 1,
      },
      { TeamIndex: 1, Players: {}, Length: 0 },
      { TeamIndex: 2, Players: {}, Length: 0 },
      { TeamIndex: 3, Players: {}, Length: 0 },
      { TeamIndex: 4, Players: {}, Length: 0 },
    ],
    LeaderID: accountId,
    LobbyType: 0,
    ReadyPlayers: {},
    PlayerGameplayPreferences: { [accountId]: gameplayPrefs },
    PlayerAutoPartyPreferences: { [accountId]: false },
    GameVersion: env.GAME_VERSION,
    HissCrc: 1167552915,
    Platforms: { [accountId]: "PC" },
    AllMultiplayParams: {
      "1": {
        MultiplayClusterSlug: "ec2-us-east-1-dokken",
        MultiplayProfileId: "1252499",
        MultiplayRegionId: "",
      },
      "2": {
        MultiplayClusterSlug: "ec2-us-east-1-dokken",
        MultiplayProfileId: "1252922",
        MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
      },
      "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
      "4": {
        MultiplayClusterSlug: "ec2-us-east-1-dokken",
        MultiplayProfileId: "1252928",
        MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
      },
    },
    LockedLoadouts: { [accountId]: { Character: character, Skin: skin } },
    IsLobbyJoinable: true,
    MatchID: ObjectID().toHexString(),
  };
}

async function saveLobbyToRedis(lobby: BaseLobby) {
  const key = lobbyKey(lobby.MatchID);
  await redisClient.set(key, JSON.stringify(lobby), { EX: LOBBY_EX });
}

export async function createPartyLobby(
  accountId: string,
  lobbyMode: MATCH_TYPES = MATCH_TYPES.ONE_V_ONE,
): Promise<PartyLobby> {
  const baseLobby = await createBaseLobby(accountId);
  const partyLobby: PartyLobby = {
    ...baseLobby,
    ModeString: lobbyMode,
  };
  // NOTE: Do NOT save to custom_lobby_ssc: namespace — this is a party lobby, not a custom lobby.
  // The game will call create_party_lobby SSC next, which goes to the old handler and creates
  // proper Redis state (lobby:{id}, player_lobby:{id}, etc.).
  // We just build the response object here so the leave_player_lobby SSC can return it.
  logger.info(`${logPrefix} Creating party lobby for ${accountId} - matchLobbyId:${partyLobby.MatchID}`);
  return partyLobby;
}

export async function setLobbyMode(leaderId: string, lobbyId: string, newMode: MATCH_TYPES) {
  await evalLua(LUA_SET_LOBBY_MODE, [lobbyKey(lobbyId)], [leaderId, newMode]);
  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "OnLobbyModeUpdated",
      LobbyId: lobbyId,
      ModeString: newMode,
    },
    payload: { custom_notification: "realtime" },
    header: "",
    cmd: "update",
  });
}

export async function lockLobby(lobbyId: string, leaderId: string) {
  await evalLua(LUA_LOCK_LOBBY, [lobbyKey(lobbyId)], [leaderId]);
}

export async function leaveLobby(lobbyId: string, accountId: string, createParty = true) {
  const lobby = await getLobby(lobbyId);
  let newLobby: BaseLobby | null = null;
  if (!lobby) return null;

  // Clean up player→lobby tracking
  await redisClient.del(`ssc_custom_lobby_player:${accountId}`);

  if (createParty) {
    newLobby = await createPartyLobby(accountId);
  }

  if ((lobby as CustomLobby).match_config) {
    const result = await evalLua(LUA_LEAVE_CUSTOM_LOBBY, [lobbyKey(lobbyId)], [accountId]);
    if (result) {
      const { playerData, readyPlayers, leaderID, gameModeSlug } = parseLuaResult<any>(result as string)!;

      await Promise.all([
        gameModeSlug ? refreshPlayerBuffs(lobbyId, gameModeSlug) : Promise.resolve(),
        broadcastToTopic(lobbyId, {
          data: {
            MatchID: lobbyId,
            template_id: "PlayerLeftLobby",
            Player: playerData,
            ReadyPlayers: readyPlayers,
            NewLeader: leaderID,
          },
          payload: {
            custom_notification: "realtime",
            match: { id: lobbyId },
          },
          cmd: "update",
          header: "",
        }),
      ]);
    }
  } else {
    await redisClient.del(lobbyKey(lobbyId));
  }
  return newLobby;
}

// ─── Custom Lobby Functions ──────────────────────────────────────────────────

export function getGameModeMaps(gameModeSlug: keyof typeof GAME_MODES_CONFIG) {
  const mapRotationName = (GAME_MODES_CONFIG[gameModeSlug] as any).data.GameModeData
    .MapRotation as keyof typeof MAP_ROTATIONS;
  const mapRotation = (MAP_ROTATIONS[mapRotationName] as any)?.data?.MapsInRotation?.map(
    (map: any) => ({ Map: map.Map, IsSelected: true }),
  );
  return mapRotation || [];
}

export function getWorldBuffs(gameModeSlug: keyof typeof GAME_MODES_CONFIG): string[] {
  return (GAME_MODES_CONFIG[gameModeSlug] as any).data.GameModeData.RequiredWorldBuffs ?? [];
}

export function getCustomLobbyDefaultSettings(
  gameModeSlug: keyof typeof GAME_MODES_CONFIG,
): CustomLobbySettings {
  const defaultMapConfig = (GAME_MODES_CONFIG[gameModeSlug] as any).data;
  return {
    GameModeSlug: gameModeSlug,
    match_config: {
      TeamStyle: defaultMapConfig.GameModeData.TeamStyle as TeamStyle,
      QueueType: "Unselected",
      Context: "Custom",
      ModeDifficulty: "Unselected",
      GameModeAlias: "Versus",
      NumRingoutsForWin: defaultMapConfig.GameModeData.GameModeTeams[0].NumRingouts,
      MatchDuration: defaultMapConfig.GameModeData.MatchDuration,
      AllowHazards: defaultMapConfig.GameModeData.bMapHazards,
      AllowDuplicateCharacters: true,
      AreRewardsSkipped: true,
      num_set_wins_required: 1,
      EnableShields: 1,
    },
    Maps: getGameModeMaps(gameModeSlug),
    WorldBuffs: getWorldBuffs(gameModeSlug),
    PlayerBuffs: {},
    Handicaps: {},
  };
}

export async function createCustomLobby(accountId: string): Promise<CustomLobby> {
  const baseLobby = await createBaseLobby(accountId);
  const customLobby: CustomLobby = {
    ...baseLobby,
    ReadyPlayers: { [accountId]: true },
    ...getCustomLobbyDefaultSettings("gm_classic_2v2"),
  };
  await saveLobbyToRedis(customLobby);
  await redisClient.set(`ssc_custom_lobby_player:${accountId}`, customLobby.MatchID, { EX: LOBBY_EX });
  await redisClient.publish(
    LOBBY_JOINED_CHANNEL,
    JSON.stringify({ lobbyId: customLobby.MatchID, accountId }),
  );
  logger.info(`${logPrefix} Creating custom lobby for ${accountId} - matchLobbyId:${customLobby.MatchID}`);
  return customLobby;
}

export async function updateTeamStyleForCustomLobby(
  lobbyId: string,
  leaderId: string,
  newStyle: TeamStyle,
) {
  const gameModeSlug = TEAM_STYLE_TO_GAME_MODE[newStyle] ?? "gm_classic_2v2";
  const lobby = await applyGameSettings(lobbyId, leaderId, gameModeSlug);
  if (!lobby) return null;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [leaderId],
    {
      data: {
        template_id: "TeamStyleChangedForCustomGame",
        MatchID: lobbyId,
        lobby,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return lobby;
}

export async function updateGameModeForCustomLobby(
  lobbyId: string,
  leaderId: string,
  gameModeSlug: keyof typeof GAME_MODES_CONFIG,
) {
  await sleep(1500);
  const lobby = await applyGameSettings(lobbyId, leaderId, gameModeSlug);
  if (!lobby) return null;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [leaderId],
    {
      data: {
        template_id: "GameModeUpdatedForCustomGame",
        MatchID: lobbyId,
        lobby,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return lobby;
}

export async function updateIntSettingForCustomLobby(
  lobbyId: string,
  leaderId: string,
  settingKey: keyof CustomLobbyMatchConfig,
  settingValue: any,
) {
  const result = await evalLua(
    LUA_UPDATE_INT_SETTING,
    [lobbyKey(lobbyId)],
    [leaderId, settingKey, JSON.stringify(settingValue)],
  );
  if (!result) return null;
  const lobby = parseLuaResult<CustomLobby>(result as string)!;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [leaderId],
    {
      data: {
        template_id: "IntSettingUpdatedForCustomGame",
        MatchID: lobbyId,
        SettingKey: settingKey,
        SettingValue: settingValue,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return lobby;
}

export async function updateEnabledMapsForCustomLobby(
  lobbyId: string,
  leaderId: string,
  mapsSlugs: string[],
) {
  const result = await evalLua(
    LUA_UPDATE_ENABLED_MAPS,
    [lobbyKey(lobbyId)],
    [leaderId, ...mapsSlugs],
  );
  if (!result) return null;
  const Maps = parseLuaResult<any>(result as string);

  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "MapsSetForCustomGame",
      MatchID: lobbyId,
      Maps,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return Maps;
}

export async function updateHandicapsForCustomLobby(
  lobbyId: string,
  ownerId: string,
  playerHandicap: number,
  playerId: string,
) {
  if (ownerId !== playerId) return null;
  const result = await evalLua(
    LUA_UPDATE_HANDICAP,
    [lobbyKey(lobbyId)],
    [playerId, JSON.stringify(playerHandicap)],
  );
  if (!result) return null;
  const lobby = parseLuaResult<CustomLobby>(result as string)!;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [playerId],
    {
      data: {
        MatchID: lobbyId,
        template_id: "PlayerHandicapSetForCustomGame",
        Handicaps: lobby.Handicaps,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return lobby.Handicaps;
}

export async function setWorldBuffsForCustomLobby(
  lobbyId: string,
  leaderId: string,
  worldBuffSlugs: string[],
) {
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (!raw) return null;
  const lobbyData = fixCjsonEmptyTables(JSON.parse(raw)) as CustomLobby;
  const gameModeSlug = lobbyData.GameModeSlug;
  if (!gameModeSlug) return null;

  const requiredBuffs = getWorldBuffs(gameModeSlug);
  const merged = [...new Set([...requiredBuffs, ...worldBuffSlugs])];

  const result = await evalLua(
    LUA_SET_WORLD_BUFFS,
    [lobbyKey(lobbyId)],
    [leaderId, JSON.stringify(merged)],
  );
  if (!result) return null;

  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "WorldBuffsSetForCustomGame",
      MatchID: lobbyId,
      WorldBuffs: merged,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return merged;
}

export async function switchTeamForCustomLobby(
  lobbyId: string,
  playerId: string,
  teamIndex: number,
) {
  const result = await evalLua(
    LUA_SWITCH_TEAM,
    [lobbyKey(lobbyId)],
    [playerId, teamIndex.toString()],
  );
  if (!result) return null;
  const { playerData, gameModeSlug } = parseLuaResult<any>(result as string)!;
  await refreshPlayerBuffs(lobbyId, gameModeSlug);

  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "PlayerSwitchedCustomLobbyTeams",
      MatchID: lobbyId,
      Player: playerData,
      TeamIndex: teamIndex,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return playerData;
}

export async function addCustomGameBot(
  lobbyId: string,
  leaderId: string,
  teamIndex: number,
  botConfig: {
    BotAccountID: string;
    BotSettingSlug: string;
    Fighter: { AssetPath: string; Slug: string };
    Skin: { AssetPath: string; Slug: string };
  },
) {
  const pdata = {
    Account: { id: botConfig.BotAccountID },
    AccountID: botConfig.BotAccountID,
    BotSettingSlug: botConfig.BotSettingSlug,
    Fighter: botConfig.Fighter,
    Skin: botConfig.Skin,
    JoinedAt: new Date().toISOString(),
    CrossplayPreference: 0,
  };

  const result = await evalLua(
    LUA_ADD_CUSTOM_GAME_BOT,
    [lobbyKey(lobbyId)],
    [leaderId, botConfig.BotAccountID, teamIndex.toString(), JSON.stringify(pdata)],
  );
  if (!result) return null;
  const { playerData, gameModeSlug } = parseLuaResult<any>(result as string)!;

  await Promise.all([
    refreshPlayerBuffs(lobbyId, gameModeSlug),
    broadcastToTopic(lobbyId, {
      data: {
        MatchID: lobbyId,
        template_id: "BotAddedToCustomGame",
        TeamIndex: teamIndex,
        Bot: playerData,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    }),
  ]);
  return playerData;
}

export async function updateCustomGameBotFighter(
  lobbyId: string,
  leaderId: string,
  botAccountId: string,
  fighter: { AssetPath: string; Slug: string },
  skin: { AssetPath: string; Slug: string },
  botSettingSlug: string,
) {
  const result = await evalLua(
    LUA_UPDATE_BOT_FIGHTER,
    [lobbyKey(lobbyId)],
    [leaderId, botAccountId, JSON.stringify(fighter), JSON.stringify(skin), botSettingSlug],
  );
  if (!result) return null;
  const bot = parseLuaResult<any>(result as string)!;

  await broadcastToTopic(lobbyId, {
    data: {
      MatchID: lobbyId,
      template_id: "BotSettingsUpdatedForCustomGame",
      Bot: bot,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return bot;
}

export async function resetCustomLobbySettings(lobbyId: string, leaderId: string) {
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (!raw) return null;
  const lobbyData = fixCjsonEmptyTables(JSON.parse(raw)) as CustomLobby;
  const gameModeSlug = lobbyData.GameModeSlug;
  if (!gameModeSlug) return null;

  const lobby = await applyGameSettings(lobbyId, leaderId, gameModeSlug);
  if (!lobby) return null;

  const data = {
    MatchID: lobbyId,
    GameModeSlug: lobby.GameModeSlug,
    MatchConfig: lobby.match_config,
    Maps: lobby.Maps,
  };

  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "CustomGameResetToDefaults",
      ...data,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return data;
}

export async function joinCustomLobby(lobbyId: string, accountId: string, isSpectator: boolean) {
  const playerConfig = await getPlayerConfig(accountId);
  const lockedLoadout = playerConfig
    ? JSON.stringify({ Character: playerConfig.Character, Skin: playerConfig.Skin })
    : "";

  const result = await evalLua(
    LUA_JOIN_CUSTOM_LOBBY,
    [lobbyKey(lobbyId)],
    [accountId, isSpectator ? "true" : "false", new Date().toISOString(), lockedLoadout],
  );
  if (!result) return null;
  const raw = result as string;
  if (raw === "false") return null;

  const lobby = fixCjsonEmptyTables(JSON.parse(raw)) as CustomLobby;

  await refreshPlayerBuffs(lobbyId, lobby.GameModeSlug);
  const updatedLobby = await getLobby(lobbyId);

  const playerTeam = lobby.Teams.find((t) => t.Players[accountId]);
  const cluster = lobby.AllMultiplayParams["1"]?.MultiplayClusterSlug ?? "";

  await redisClient.set(`ssc_custom_lobby_player:${accountId}`, lobby.MatchID, { EX: LOBBY_EX });
  await redisClient.publish(
    LOBBY_JOINED_CHANNEL,
    JSON.stringify({ lobbyId: lobby.MatchID, accountId }),
  );

  if (!updatedLobby) return null;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [accountId],
    {
      data: {
        MatchID: lobbyId,
        template_id: "PlayerJoinedLobby",
        Player: playerTeam?.Players[accountId],
        TeamIndex: playerTeam?.TeamIndex,
        Cluster: cluster,
        LockedLoadouts: lobby.LockedLoadouts,
        ModeString: null,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return updatedLobby;
}

export async function setPlayerReady(lobbyId: string, PlayerID: string, Ready: boolean) {
  const result = await evalLua(
    LUA_SET_PLAYER_READY,
    [lobbyKey(lobbyId)],
    [PlayerID, Ready ? "true" : "false"],
  );
  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby) return null;

  const data = {
    MatchID: lobbyId,
    PlayerID,
    Ready,
    bAllPlayersReady: result === 1,
  };

  await broadcastToTopic(lobbyId, {
    data: {
      template_id: "PlayerReadyForLobby",
      ...data,
    },
    payload: {
      custom_notification: "realtime",
      match: { id: lobbyId },
    },
    cmd: "update",
    header: "",
  });
  return data;
}

export async function kickFromLobby(lobbyId: string, leaderId: string, kickeeId: string) {
  // Get remaining player IDs BEFORE the kick (so we can notify kicked player too)
  const lobbyBeforeKick = await getLobby(lobbyId);
  const allPlayerIds = lobbyBeforeKick ? Object.keys(lobbyBeforeKick.PlayerGameplayPreferences) : [];

  const result = await evalLua(LUA_KICK_FROM_LOBBY, [lobbyKey(lobbyId)], [leaderId, kickeeId]);
  if (!result) return null;
  const { playerData: player, gameModeSlug } = parseLuaResult<any>(result as string)!;

  // Send kick notification to ALL players including the kicked player
  const msg: CustomLobbyNotificationMessage = {
    targetPlayerIds: allPlayerIds,
    excludePlayerIds: [],
    notification: {
      data: {
        MatchID: lobbyId,
        template_id: "PlayerKickedFromLobby",
        Player: player,
        KickeeAccountID: kickeeId,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  };
  await redisClient.publish(CUSTOM_LOBBY_NOTIFICATION_CHANNEL, JSON.stringify(msg));

  await refreshPlayerBuffs(lobbyId, gameModeSlug);
  await redisClient.del(`ssc_custom_lobby_player:${kickeeId}`);

  return player;
}

export async function promoteToLobbyLeader(
  lobbyId: string,
  leaderID: string,
  promoteTarget: string,
) {
  const result = await evalLua(LUA_PROMOTE_LEADER, [lobbyKey(lobbyId)], [promoteTarget]);
  if (!result) return null;
  const lobby = parseLuaResult<CustomLobby>(result as string)!;

  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [leaderID],
    {
      data: {
        MatchID: lobbyId,
        template_id: "LobbyLeaderChanged",
        LeaderID: promoteTarget,
        ReadyPlayers: lobby.ReadyPlayers,
      },
      payload: {
        custom_notification: "realtime",
        match: { id: lobbyId },
      },
      cmd: "update",
      header: "",
    },
  );
  return { MatchID: lobbyId, LeaderID: promoteTarget, ReadyPlayers: lobby.ReadyPlayers };
}

export async function generateLobbyCode(lobbyId: string, leaderId: string) {
  const lobby = await getLobby(lobbyId);
  if (!lobby || lobby.LeaderID !== leaderId) return null;

  const code = generateCode(5);
  await redisClient.set(`lobby_code:${code}`, lobbyId, { NX: true, EX: LOBBY_EX });

  // Update lobby with LobbyCode
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (raw) {
    const lobbyData = JSON.parse(raw);
    lobbyData.LobbyCode = code;
    await redisClient.set(lobbyKey(lobbyId), JSON.stringify(lobbyData), { EX: LOBBY_EX });
  }
  return code;
}

export async function getLobbyIdFromCode(code: string) {
  return await redisClient.get(`lobby_code:${code}`);
}

export async function updatePlayerLoadout(
  playerId: string,
  lobbyId: string,
  character: string,
  skin: string,
) {
  const lobby = await getLobby(lobbyId);
  if (!lobby) return;

  // Update the locked loadout in the lobby
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (raw) {
    const lobbyData = JSON.parse(raw);
    if (!lobbyData.LockedLoadouts) lobbyData.LockedLoadouts = {};
    lobbyData.LockedLoadouts[playerId] = { Character: character, Skin: skin };
    await redisClient.set(lobbyKey(lobbyId), JSON.stringify(lobbyData), { EX: LOBBY_EX });
  }

  // Update player Redis hash
  await redisClient.hSet(`player:${playerId}`, { character, skin });

  // Broadcast loadout change to other players
  await broadcastToUsers(
    Object.keys(lobby.PlayerGameplayPreferences),
    [playerId],
    {
      data: {
        Loadout: { Character: character, Skin: skin },
        AccountId: playerId,
        LobbyId: lobbyId,
        template_id: "OnPlayerLoadoutLocked",
        bAreAllLoadoutsLocked: true,
      },
      payload: {
        match: { id: lobbyId },
        custom_notification: "realtime",
      },
      header: "",
      cmd: "update",
    },
  );
}

export async function startCustomMatch(lobbyId: string, leaderId: string) {
  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby || lobby.LeaderID !== leaderId) return null;

  const matchId = ObjectID().toHexString();
  const resultId = ObjectID().toHexString();
  const selectedMaps = lobby.Maps.filter((m) => m.IsSelected);
  const randomMap = selectedMaps[Math.floor(Math.random() * selectedMaps.length)] ?? lobby.Maps[0];

  const playerConfigs = await getPlayersConfig(Object.keys(lobby.PlayerGameplayPreferences));

  const Players: Record<string, PlayerConfig> = {};
  const Spectators: Record<string, PlayerConfig> = {};

  for (const team of lobby.Teams) {
    const isSpectatorTeam = team.TeamIndex === 4;
    for (const [playerId, lobbyPlayer] of Object.entries(team.Players)) {
      const isBot = lobbyPlayer.BotSettingSlug !== "";
      const buffs = lobby.PlayerBuffs?.[playerId] ?? [];
      const handicap = lobby.Handicaps?.[playerId] ?? 0;

      let playerConfig: PlayerConfig;

      if (isBot) {
        const diff = BOT_DIFFICULTY[lobbyPlayer.BotSettingSlug] ?? BOT_DIFFICULTY.Medium;
        playerConfig = {
          AccountId: playerId,
          Username: playerId,
          bUseCharacterDisplayName: true,
          PlayerIndex: lobbyPlayer.LobbyPlayerIndex,
          TeamIndex: team.TeamIndex,
          Character: lobbyPlayer.Fighter?.Slug ?? "",
          Skin: lobbyPlayer.Skin?.Slug ?? "",
          Taunts: [],
          Perks: [...BOT_DEFAULT_PERKS],
          Banner: "banner_default",
          ProfileIcon: "",
          RingoutVfx: "ring_out_vfx_default",
          bIsBot: true,
          BotBehaviorOverride: "",
          BotDifficultyMin: diff.min,
          BotDifficultyMax: diff.max,
          Buffs: buffs,
          StatTrackers: [],
          Gems: [],
          StartingDamage: 0,
          Handicap: handicap,
          GameplayPreferences: 0,
          bAutoPartyPreference: false,
          PartyMember: null,
          PartyId: null,
          RankedTier: null,
          RankedDivision: null,
          WinStreak: null,
          IsHost: false,
          Ip: "",
        };
      } else {
        const config = playerConfigs.get(playerId);
        if (!config) continue;
        // Read fresh character/skin from Redis (set by lock_lobby_loadout SSC)
        // Priority: player:{id} → connections:{id} → LockedLoadouts → config defaults
        const freshPlayer = await redisClient.hGetAll(`player:${playerId}`);
        const freshConn = await redisClient.hGetAll(`connections:${playerId}`);
        const lobbyLoadout = lobby.LockedLoadouts[playerId];
        const character = freshPlayer?.character || freshConn?.character || lobbyLoadout?.Character || config.Character;
        const skin = freshPlayer?.skin || freshConn?.skin || lobbyLoadout?.Skin || config.Skin;

        // Sync to connections hash so WebSocket match config can read them
        if (character && skin) {
          await redisClient.hSet(`connections:${playerId}`, { character, skin });
          const ip = freshConn?.current_ip;
          if (ip) await redisClient.hSet(`connections:${ip}`, { character, skin });
        }

        playerConfig = {
          ...config,
          PlayerIndex: lobbyPlayer.LobbyPlayerIndex,
          TeamIndex: team.TeamIndex,
          Character: character,
          Skin: skin,
          Buffs: buffs,
          Handicap: handicap,
          GameplayPreferences: Number(
            lobby.PlayerGameplayPreferences?.[playerId] ?? config.GameplayPreferences,
          ),
          PartyId: null,
          PartyMember: null,
          IsHost: playerId === lobby.LeaderID,
        };
      }

      if (isSpectatorTeam) {
        Spectators[playerId] = playerConfig;
      } else {
        Players[playerId] = playerConfig;
      }
    }
  }

  // ── Build RedisTeamEntry[] using live server's format ──
  // Interleaved spawn: playerIndex = idxInTeam * 2 + teamIndex
  // Team 0 → spawns 0, 2  |  Team 1 → spawns 1, 3
  const matchmakingRequestId = ObjectID().toHexString();
  const teamEntries: RedisTeamEntry[] = [];
  const spectatorEntries: RedisTeamEntry[] = [];
  let globalIdx = 0;

  for (const team of lobby.Teams) {
    if (team.TeamIndex === 4) {
      // Spectators — each gets a unique sentinel PlayerIndex starting at 8888.
      // The rollback uses PlayerIndex to look up the player in match config; if
      // multiple specs share the same index, the rollback collapses them onto
      // one PlayerInfo entry and only one spec actually loads in.
      let specIdx = 0;
      for (const [playerId, lobbyPlayer] of Object.entries(team.Players)) {
        if (lobbyPlayer.BotSettingSlug !== "") continue; // skip bots in spectator
        const config = Players[playerId] || Spectators[playerId];
        spectatorEntries.push({
          playerId,
          partyId: matchId,
          playerIndex: 8888 + specIdx, // 8888, 8889, 8890, ... — each spec unique
          teamIndex: 0 as 0 | 1,
          isHost: false,
          ip: config?.Ip || "",
          isSpectator: true,
        });
        specIdx++;
      }
    } else if (team.TeamIndex >= 0 && team.TeamIndex <= 3) {
      let idxInTeam = 0;
      // Sort entries so humans take the LOW PlayerIndex slots within the team
      // before bots. Otherwise — because Object.entries iterates in insertion
      // order — a bot added to the team first ends up with PlayerIndex 0 and
      // the human gets PlayerIndex 2, which then crashes the rollback because
      // it sizes match.Inputs by the human count and indexes by PlayerIndex.
      const sortedEntries = Object.entries(team.Players).sort(([, a], [, b]) => {
        const aBot = a.BotSettingSlug !== "" ? 1 : 0;
        const bBot = b.BotSettingSlug !== "" ? 1 : 0;
        return aBot - bBot;
      });
      for (const [playerId, lobbyPlayer] of sortedEntries) {
        const config = Players[playerId];
        if (!config) continue;
        const playerIndex = idxInTeam * 2 + team.TeamIndex;
        const isBot = lobbyPlayer.BotSettingSlug !== "";
        teamEntries.push({
          playerId,
          partyId: matchId,
          playerIndex,
          teamIndex: team.TeamIndex as any,
          isHost: !isBot && globalIdx === 0, // first NON-BOT player is host (bots can't host)
          ip: config.Ip || "",
          isBot,
        });
        idxInTeam++;
        if (!isBot) globalIdx++; // only humans count toward host-selection ordering

        // Stash bot config in Redis so the WebSocket gameplay-config builder
        // can produce a PlayerConfig for the bot without doing any DB/cosmetics
        // lookups (bot IDs aren't valid ObjectIds and have no Cosmetics row).
        if (isBot) {
          const diff = BOT_DIFFICULTY[lobbyPlayer.BotSettingSlug] ?? BOT_DIFFICULTY.Medium;
          await redisClient.hSet(`bot_config:${playerId}`, {
            character: lobbyPlayer.Fighter?.Slug ?? "character_jason",
            skin: lobbyPlayer.Skin?.Slug ?? "skin_jason_000",
            difficultyMin: String(diff.min),
            difficultyMax: String(diff.max),
            settingSlug: lobbyPlayer.BotSettingSlug,
          });
          await redisClient.expire(`bot_config:${playerId}`, 86400);
        }
      }
    }
  }

  const allEntries = [...teamEntries, ...spectatorEntries];
  const allPlayerIds = allEntries.map((p) => p.playerId);

  // Build ticket for perk-locking handler
  const ticket: RedisMatchTicket = {
    party_size: teamEntries.length,
    players: teamEntries.map((p) => ({
      id: p.playerId,
      skill: 0,
      region: "local",
    })),
    created_at: Date.now(),
    partyId: matchId,
    matchmakingRequestId,
    isPasswordMatch: true,
  };

  // Store match in Redis
  let tempCustomLobbyRollbackPort: number = 0;
  if (useOnDemandRollback)
  {
    //tempCustomLobbyRollbackPort = randomInt(customLobbyUDPPortLow, customLobbyUDPPortHigh);
    tempCustomLobbyRollbackPort = (await DeployInfo.getNextRollbackPort(true)) || DeployInfo.getRandomRollbackPort();
  }
  else
  {
    tempCustomLobbyRollbackPort = randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH);
  }

  const rollbackPort = tempCustomLobbyRollbackPort;
  const match: RedisMatch = {
    matchId,
    resultId,
    tickets: [ticket],
    status: "pending",
    createdAt: Date.now(),
    matchType: lobby.match_config.TeamStyle === "Solos" ? "1v1" : lobby.match_config.TeamStyle === "FFA" ? "FFA" : "2v2",
    totalPlayers: teamEntries.length,
    rollbackPort,
    isPasswordMatch: true, // Custom lobby = no ELO
  };
  await redisUpdateMatch(matchId, match);

  // Pre-lock perks for bots — they don't send /ssc/invoke/perks_lock, so without
  // this the all-perks-locked check in handleSsc_invoke_perks_lock would never
  // pass and the match stays stuck in pregame.
  const botPerks = [...BOT_DEFAULT_PERKS];
  for (const entry of teamEntries) {
    if (entry.isBot) {
      await redisLockPerks({
        containerMatchId: matchId,
        playerId: entry.playerId,
        perks: botPerks,
      });
    }
  }

  // Select map
  const map = randomMap?.Map ?? "M003_V1";

  if (useOnDemandRollback) {
    logwrapper.info(`${logPrefix} Deploying rollback server for match ${matchId} with port: ${match.rollbackPort}`)
    let deployInfo: IDeployInfo = getDefaultDeployInfo();
    deployInfo.port = rollbackPort;
    deployInfo.entrypoint = deployInfo.entrypoint.replace("CHANGEMEDEFAULTPORT", deployInfo.port.toString());
    deployInfo.ovs_server = env.OVS_SERVER;
    const isDeployed = DeployInfo.Deploy(deployInfo);
    if (!isDeployed) {
      logger.error(`${logPrefix} Failed to deploy rollback server for match ${matchId} on port ${deployInfo.port}`);
      // Depending on the desired behavior, you might want to:
      // - Mark the match as failed and notify players
      // - Fall back to an internal rollback server if available
      // For now, we'll just log the error and proceed, but this means the match will likely fail when clients try to connect.
    }
  }

  // Collect player buffs for injection into match config
  const playerBuffsMap: Record<string, string[]> = {};
  for (const [pid, config] of Object.entries(Players)) {
    if (config.Buffs && config.Buffs.length > 0) {
      playerBuffsMap[pid] = config.Buffs;
    }
  }

  // Build MATCH_FOUND_NOTIFICATION with custom game settings for injection
  const notification: MATCH_FOUND_NOTIFICATION = {
    players: allEntries,
    matchId,
    matchKey: randomBytes(32).toString("base64"),
    map,
    mode: lobby.match_config.TeamStyle === "Solos" ? "1v1" : lobby.match_config.TeamStyle === "FFA" ? "FFA" : "2v2",
    rollbackPort,
    isCustomGame: true,
    customNumRingouts: lobby.match_config.NumRingoutsForWin,
    customMatchTime: lobby.match_config.MatchDuration,
    customHazards: lobby.match_config.AllowHazards,
    customShields: Boolean(lobby.match_config.EnableShields),
    worldBuffs: lobby.WorldBuffs,
    playerBuffs: playerBuffsMap,
  };

  // Fire the match notification pipeline
  await redisOnGameplayConfigNotified(notification);
  await redisMatchMakingComplete(matchId, matchmakingRequestId, allPlayerIds);

  // Track matchId → lobbyId for rematch handling
  await redisClient.set(`ssc_custom_lobby_match:${matchId}`, lobbyId, { EX: 1200 });
  // Track playerId → lobbyId for rematch accept/decline
  for (const pid of allPlayerIds) {
    await redisClient.set(`ssc_custom_lobby_player:${pid}`, lobbyId, { EX: 1200 });
  }

  logger.info(`${logPrefix} Custom match started: ${matchId} on map ${map} with ${allPlayerIds.length} players`);
  return match;
}

// ─── Rematch System ──────────────────────────────────────────────────────────

export async function getSscCustomLobbyForMatch(matchId: string): Promise<string | null> {
  return await redisClient.get(`ssc_custom_lobby_match:${matchId}`);
}

export async function handleSscCustomLobbyMatchEnd(matchId: string): Promise<void> {
  const lobbyId = await getSscCustomLobbyForMatch(matchId);
  if (!lobbyId) return;

  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby) return;

  // Clear all ready states
  const raw = await redisClient.get(lobbyKey(lobbyId));
  if (raw) {
    const lobbyData = JSON.parse(raw);
    lobbyData.ReadyPlayers = {};
    await redisClient.set(lobbyKey(lobbyId), JSON.stringify(lobbyData), { EX: LOBBY_EX });
  }

  logger.info(`${logPrefix} Match ${matchId} ended for SSC custom lobby ${lobbyId}, starting 25s rematch timer`);

  // Clean up match→lobby mapping
  await redisClient.del(`ssc_custom_lobby_match:${matchId}`);

  // Start 25s rematch timer
  const rematchTimerKey = `ssc_custom_lobby_rematch_timer:${lobbyId}`;
  await redisClient.set(rematchTimerKey, matchId, { EX: 30 });

  setTimeout(async () => {
    try {
      const timerStillActive = await redisClient.get(rematchTimerKey);
      if (!timerStillActive) {
        logger.info(`${logPrefix} Rematch timer cancelled for SSC lobby ${lobbyId}`);
        return;
      }
      await redisClient.del(rematchTimerKey);
      await redisClient.del(`ssc_custom_lobby_rematch_accept:${lobbyId}`);
      await triggerSscRematch(lobbyId);
    } catch (e) {
      logger.error(`${logPrefix} Error in rematch timer for SSC lobby ${lobbyId}: ${e}`);
    }
  }, 25000);
}

export async function handleSscRematchAccept(playerId: string): Promise<void> {
  const lobbyId = await redisClient.get(`ssc_custom_lobby_player:${playerId}`);
  if (!lobbyId) return;

  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby) return;

  const rematchTimerKey = `ssc_custom_lobby_rematch_timer:${lobbyId}`;
  const timerActive = await redisClient.get(rematchTimerKey);
  if (!timerActive) return;

  const acceptKey = `ssc_custom_lobby_rematch_accept:${lobbyId}`;
  await redisClient.sAdd(acceptKey, playerId);
  await redisClient.expire(acceptKey, 30);

  logger.info(`${logPrefix} Player ${playerId} accepted rematch in SSC lobby ${lobbyId}`);

  // Count total non-spectator HUMAN players (bots can't accept rematch)
  let totalPlayers = 0;
  for (const team of lobby.Teams) {
    if (team.TeamIndex === 4) continue;
    for (const lp of Object.values(team.Players)) {
      if (lp.BotSettingSlug === "") totalPlayers++;
    }
  }

  const acceptedCount = await redisClient.sCard(acceptKey);
  if (acceptedCount >= totalPlayers) {
    logger.info(`${logPrefix} All ${totalPlayers} human players accepted rematch in SSC lobby ${lobbyId} — triggering immediately`);
    await redisClient.del(rematchTimerKey);
    await redisClient.del(acceptKey);
    await triggerSscRematch(lobbyId);
  }
}

export async function handleSscRematchDecline(playerId: string): Promise<string[]> {
  const lobbyId = await redisClient.get(`ssc_custom_lobby_player:${playerId}`);
  if (!lobbyId) return [];

  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby) return [];

  logger.info(`${logPrefix} Player ${playerId} declined rematch in SSC lobby ${lobbyId}`);

  await redisClient.del(`ssc_custom_lobby_rematch_timer:${lobbyId}`);
  await redisClient.del(`ssc_custom_lobby_rematch_accept:${lobbyId}`);

  // Collect all player IDs and clean up their lobby tracking
  const allPlayerIds: string[] = [];
  for (const team of lobby.Teams) {
    for (const pid of Object.keys(team.Players)) {
      allPlayerIds.push(pid);
    }
  }

  // Don't delete lobby or player tracking here — players return to custom lobby UI
  // and will leave explicitly via leave_player_lobby or leave-party

  return allPlayerIds;
}

async function triggerSscRematch(lobbyId: string): Promise<void> {
  const lobby = (await getLobby(lobbyId)) as CustomLobby | null;
  if (!lobby) return;

  // Verify all players are still online
  for (const team of lobby.Teams) {
    for (const playerId of Object.keys(team.Players)) {
      if (team.Players[playerId].BotSettingSlug !== "") continue; // skip bots
      const isOnline = await redisClient.sIsMember("online_players", playerId);
      if (!isOnline) {
        logger.info(`${logPrefix} Player ${playerId} disconnected, skipping rematch for SSC lobby ${lobbyId}`);
        return;
      }
    }
  }

  logger.info(`${logPrefix} Triggering rematch for SSC lobby ${lobbyId}`);
  await startCustomMatch(lobbyId, lobby.LeaderID);
}
