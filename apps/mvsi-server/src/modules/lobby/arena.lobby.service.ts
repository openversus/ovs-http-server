import { logger } from "@mvsi/logger";
import { redisClient } from "@mvsi/redis";
import { ObjectId } from "mongodb";
import { randomUUID } from "node:crypto";
import { getRandomMap2v2 } from "../../data/maps";
import { REDIS_CHARACTER_SLUGS_KEY, getAssetsByType } from "../../loadAssets";
import type { GameplayConfig } from "../matchmaking/matchmaking.types";
import { getActiveMatch, notifyActiveMatchCreated } from "../matchmaking/matchmaking.service";
import {
  broadcastNotificationToTopic,
  broadcastNotificationToUsers,
} from "../notifications/notifications.utils";
import { generateBotId, isBotId } from "../bots/bots.service";
import { getPlayerConfig } from "../playerConfig/playerConfig.service";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";
import type { ArenaLobby, LobbyCreatedMessage } from "./lobby.types";
import { LOBBY_JOINED_CHANNEL } from "./lobby.types";
import { createBaseLobby, notifyLobbyJoined } from "./lobby.service";
import type {
  ArenaConstants,
  ArenaData,
  ArenaPlayerData,
  ArenaPlayerInfo,
  ArenaPlayerStats,
  ArenaTeamInfo,
  MatchStats,
} from "./arena.lobby.types";
import { generateShopOptions, getGemName, ALL_ARENA_ITEMS } from "../../data/arenaItemDefs";
import { INVENTORY_DEFINITIONS } from "../../data/inventoryDefs";
import { RealtimeNotificationUsersMessage } from "../notifications/notifications.types";

// ─── Constants ────────────────────────────────────────────────────────────────

const ARENA_EX = 2 * 60 * 60; // 2 hours
const TOTAL_TEAMS = 8;
const PLAYERS_PER_TEAM = 2;
const SELECTABLE_CHARACTER_COUNT = 6;

function lobbyKey(id: string) {
  return `lobby:${id}`;
}

function arenaStateKey(id: string) {
  return `arenaState:${id}`;
}

function evalLua(script: string, keys: string[], args: string[]) {
  return redisClient.eval(script, { keys, arguments: args });
}

// ─── Arena constants (tweak here to tune gameplay) ───────────────────────────

export function getArenaConstants(): ArenaConstants {
  return {
    FaceoffWaitTime: 10,
    CurrencyPerRingout: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
    MaxInterest: 10,
    ShopLevelWeights: [
      { RarityWeight: { Uncommon: 0.05, Rare: 0, Epic: 0, Legendary: 0, Common: 0.95 } },
      { RarityWeight: { Uncommon: 0.2, Rare: 0.05, Epic: 0, Legendary: 0, Common: 0.75 } },
      { RarityWeight: { Uncommon: 0.25, Rare: 0.15, Epic: 0.05, Legendary: 0, Common: 0.55 } },
      { RarityWeight: { Uncommon: 0.3, Rare: 0.2, Epic: 0.1, Legendary: 0, Common: 0.4 } },
      { RarityWeight: { Uncommon: 0.3, Rare: 0.25, Epic: 0.15, Legendary: 0.05, Common: 0.3 } },
      { RarityWeight: { Uncommon: 0.2, Rare: 0.3, Epic: 0.2, Legendary: 0.1, Common: 0.2 } },
      { RarityWeight: { Uncommon: 0.2, Rare: 0.3, Epic: 0.25, Legendary: 0.15, Common: 0.1 } },
      { RarityWeight: { Uncommon: 0.15, Rare: 0.3, Epic: 0.3, Legendary: 0.2, Common: 0.05 } },
      { RarityWeight: { Uncommon: 0.1, Rare: 0.3, Epic: 0.3, Legendary: 0.25, Common: 0.05 } },
      { RarityWeight: { Uncommon: 0.05, Rare: 0.3, Epic: 0.3, Legendary: 0.3, Common: 0.05 } },
    ],
    CurrencyForWin: 2,
    InterestPerBoost: [4, 3, 1],
    HealthRoundScalars: [1, 1, 1.1, 1.1, 1.2, 1.2, 1.4, 1.4, 1.5, 1.6, 1.7, 2, 2.5, 3],
    CurrencyForRandomCharacterSelect: 5,
    ItemXpPerLevel: [0, 1, 3],
    CurrencyForWinStreak: [0, 0, 2, 4, 6],
    CharacterSelectTime: 30,
    ItemAmountsByRarity: {
      Uncommon: 40,
      Rare: 35,
      Epic: 30,
      Legendary: 25,
      Mythic: 10,
      Common: 50,
    },
    CurrencyForLoseStreak: [0, 0, 2, 4, 6],
    RoundLength: 90,
    HealthRoundValues: [22, 24, 26, 28, 30, 31, 32, 33, 34, 35],
    ShopCharacterRerollCost: 4,
    InterestPer: 6,
    CurrencyPerRound: [15, 15, 15, 18, 18, 20],
  };
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function createMatchStats(): MatchStats {
  return {
    KnockbackMitigated: 0,
    KnockbackAdded: 0,
    HealingReceived: 0,
    Ringouts: 0,
    GreyHealthReceived: 0,
    DamagedAdded: 0,
    DamageMitigated: 0,
    Damage: 0,
  };
}

function createArenaPlayerData(): ArenaPlayerData {
  return {
    ShopRerollCost: 3,
    FreeShopRerolls: 0,
    InterestPer: 6,
    Inventory: Array(4)
      .fill(null)
      .map(() => ({ Xp: 0, Level: 0, Slug: "", NextLevelXp: 0, SellValue: 0 })),
    CurrencyAmount: 20,
  };
}

function createArenaPlayerStats(): ArenaPlayerStats {
  return {
    bRandomCharacter: false,
    ShopRerolls: 0,
    ItemsPurchased: 0,
    ItemsSold: 0,
    InterestGained: 0,
    MatchStats: createMatchStats(),
    CurrencySpent: 0,
    ItemsLeveled: 0,
  };
}

function createArenaTeamStats() {
  return {
    Wins: 0,
    WinStreak: 0,
    Losses: 0,
    LoseStreak: 0,
    Draws: 0,
    MatchStats: createMatchStats(),
  };
}

async function getRandomCharacters(count: number): Promise<string[]> {
  const raw = await redisClient.get(REDIS_CHARACTER_SLUGS_KEY);
  if (!raw) return [];
  const slugs: string[] = JSON.parse(raw);
  const shuffled = [...slugs].sort(() => Math.random() - 0.5);
  return shuffled.slice(0, Math.min(count, shuffled.length));
}

const FIGHTER_CLASS_MAP: Record<string, number> = {
  Support: 0,
  Assassin: 1,
  Bruiser: 2,
  Tank: 3,
  Mage: 4,
};

function getCharacterClass(characterSlug: string): number {
  const def = INVENTORY_DEFINITIONS[characterSlug];
  const fighterClass = (def?.data as any)?.FighterClass as string | undefined;
  return FIGHTER_CLASS_MAP[fighterClass ?? ""] ?? 2; // default Bruiser
}

function getRandomSkinForCharacter(characterSlug: string): string {
  const skins = getAssetsByType("SkinData");
  const matching = skins.filter((s) => s.character_slug === characterSlug).map((s) => s.slug);
  if (matching.length === 0) return "";
  return matching[Math.floor(Math.random() * matching.length)];
}

function getBotBuildType(charClass: number): string {
  // Tank=3, Bruiser=2 → "defense"; Assassin=1, Mage=4, Support=0 → "attack"
  return charClass === 3 || charClass === 2 ? "defense" : "attack";
}

async function onAllCharactersSelected(arenaId: string): Promise<void> {
  const stateKey = arenaStateKey(arenaId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return;

  // ── Assign bots random char/skin ──────────────────────────────────────────
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (!info.bIsBot) {
      arenaData.PlayerInfo[pid].SelectableCharacters = [];
      continue;
    }
    const chars = await getRandomCharacters(1);
    const character = chars[0] ?? "";
    const skin = character ? getRandomSkinForCharacter(character) : "";
    const charClass = getCharacterClass(character);
    arenaData.PlayerInfo[pid].Loadout = { Character: character, Skin: skin };
    arenaData.PlayerInfo[pid].CharacterClass = charClass;
    arenaData.PlayerInfo[pid].SelectableCharacters = [];
  }

  // ── Generate shops for each real player ───────────────────────────────────
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (info.bIsBot) continue;
    const shopOptions = generateShopOptions(info.CharacterClass, arenaData.CurrentRound);
    arenaData.PlayerInfo[pid].CurrentShopLocal = shopOptions;
  }

  // Persist updated arenaData
  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );

  // ── Notify each real match (deduplicated — each matchId appears in 2 teams) ─
  const notifiedMatchIds = new Set<string>();
  for (const teamInfo of Object.values(arenaData.TeamInfo)) {
    for (const matchId of teamInfo.Matches) {
      if (matchId.startsWith("Sim")) continue;
      if (notifiedMatchIds.has(matchId)) continue;
      notifiedMatchIds.add(matchId);

      const activeMatch = await getActiveMatch(matchId);
      if (!activeMatch) continue;

      // Build updated Players map: overlay character/skin from arenaData
      for (const pid of Object.keys(activeMatch.GameplayConfig.GameplayConfig.Players)) {
        activeMatch.GameplayConfig.GameplayConfig.Players[pid].Skin =
          arenaData.PlayerInfo[pid].Loadout.Skin;
        activeMatch.GameplayConfig.GameplayConfig.Players[pid].Character =
          arenaData.PlayerInfo[pid].Loadout.Character;
      }

      console.log(
        `PLAYERS`,
        JSON.stringify(activeMatch.GameplayConfig.GameplayConfig.Players, null, 2),
      );

      // Collect all ArenaPlayerInfo for players in this match
      const matchPlayerIds = Object.keys(activeMatch.GameplayConfig.GameplayConfig.Players);

      const notification: RealtimeNotificationUsersMessage = {
        exclude: [],
        users: matchPlayerIds,
        data: {
          data: {
            ArenaId: arenaId,
            MatchId: matchId,
            template_id: "ArenaInventoryLockedNotification",
            GameplayConfig: activeMatch.GameplayConfig.GameplayConfig,
            ArenaPlayerInfo: arenaData.PlayerInfo,
          },
          payload: {
            custom_notification: "realtime",
            match: { id: matchId },
          },
          cmd: "update",
          header: "",
        },
      };
      console.log("Notifying match of character selection:", JSON.stringify(notification, null, 2));
      await broadcastNotificationToUsers(notification);
    }
  }
}

async function notifyMatchReadyToStart(arenaId: string): Promise<void> {
  const stateKey = arenaStateKey(arenaId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return;

  // ── Assign bots random items ───────────────────────────────────────────────
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (!info.bIsBot) continue;
    const charClass = info.CharacterClass;
    const buildType = getBotBuildType(charClass);
    const randomItems = [...ALL_ARENA_ITEMS].sort(() => Math.random() - 0.5).slice(0, 4);
    arenaData.PlayerInfo[pid].PlayerData.Inventory = randomItems.map((item) => ({
      Slug: item.slug,
      Level: 1,
      Xp: 0,
      NextLevelXp: 1,
      SellValue: item.cost - 1,
    }));
    arenaData.PlayerInfo[pid].BotSettings = { BuildType: buildType };
  }

  // Persist
  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );

  // ── Notify each real match (deduplicated — each matchId appears in 2 teams) ─
  const notifiedMatchIds = new Set<string>();
  for (const teamInfo of Object.values(arenaData.TeamInfo)) {
    for (const matchId of teamInfo.Matches) {
      if (matchId.startsWith("Sim")) continue;
      if (notifiedMatchIds.has(matchId)) continue;
      notifiedMatchIds.add(matchId);

      const activeMatch = await getActiveMatch(matchId);
      if (!activeMatch) continue;

      const updatedPlayers = { ...activeMatch.GameplayConfig.GameplayConfig.Players };
      for (const [pid, player] of Object.entries(updatedPlayers)) {
        const info = arenaData.PlayerInfo[pid];
        if (!info) continue;
        const gems = info.PlayerData.Inventory.filter((slot) => slot.Slug !== "").map((slot) => ({
          Gem: getGemName(slot.Slug),
          ChargeLevel: Math.max(0, slot.Level - 1),
        }));
        updatedPlayers[pid] = {
          ...player,
          Character: info.Loadout.Character,
          Skin: info.Loadout.Skin,
          Gems: gems,
        };
      }

      const matchPlayerIds = Object.keys(activeMatch.GameplayConfig.GameplayConfig.Players);
      const arenaPlayerInfo: Record<string, ArenaPlayerInfo> = {};
      for (const pid of matchPlayerIds) {
        if (arenaData.PlayerInfo[pid]) {
          arenaPlayerInfo[pid] = arenaData.PlayerInfo[pid];
        }
      }

      const notification: RealtimeNotificationUsersMessage = {
        exclude: [],
        users: matchPlayerIds,
        data: {
          data: {
            ArenaId: arenaId,
            MatchId: matchId,
            template_id: "ArenaInventoryLockedNotification",
            GameplayConfig: {
              ...activeMatch.GameplayConfig,
              GameplayConfig: {
                ...activeMatch.GameplayConfig.GameplayConfig,
                Players: updatedPlayers,
                ArenaModeInfo: {
                  ...activeMatch.GameplayConfig.GameplayConfig.ArenaModeInfo,
                  bReadyToStart: true,
                },
              },
            },
            ArenaPlayerInfo: arenaPlayerInfo,
          },
          payload: {
            custom_notification: "realtime",
            match: { id: matchId },
          },
          cmd: "update",
          header: "",
        },
      };

      await broadcastNotificationToUsers(notification);
    }
  }
}

// ─── Lua scripts ──────────────────────────────────────────────────────────────

// ARGV[1]=accountId, ARGV[2]=joinedAt ISO, ARGV[3]=gameplayPrefs, ARGV[4]=autoParty,
// ARGV[5]=crossplayPref, ARGV[6]=platform
const LUA_JOIN_ARENA_LOBBY = `
local s = redis.call('JSON.GET', KEYS[1])
if not s then return nil end
local l = cjson.decode(s)
if l.IsLobbyJoinable == false then return cjson.encode(false) end

local accountId   = ARGV[1]
local ts          = ARGV[2]
local gameplayPrefs = tonumber(ARGV[3])
local autoParty   = ARGV[4] == 'true'
local crossplay   = tonumber(ARGV[5])
local platform    = ARGV[6]

-- Already in lobby?
for _, team in ipairs(l.Teams) do
  if team.Players[accountId] then return redis.call('JSON.GET', KEYS[1]) end
end

-- Count total players for LobbyPlayerIndex
local totalPlayers = 0
for _, team in ipairs(l.Teams) do
  totalPlayers = totalPlayers + team.Length
end

-- Find first team with a free slot (< 2 players)
local teamJsonIdx = nil
for i, team in ipairs(l.Teams) do
  if team.Length < 2 then
    teamJsonIdx = i - 1
    break
  end
end
if teamJsonIdx == nil then return nil end

local pdata = cjson.encode({
  Account           = { id = accountId },
  JoinedAt          = ts,
  BotSettingSlug    = '',
  LobbyPlayerIndex  = totalPlayers,
  CrossplayPreference = crossplay
})

redis.call('JSON.SET',       KEYS[1], '$.Teams[' .. teamJsonIdx .. '].Players.' .. accountId, pdata)
redis.call('JSON.NUMINCRBY', KEYS[1], '$.Teams[' .. teamJsonIdx .. '].Length', 1)
redis.call('JSON.SET',       KEYS[1], '$.PlayerGameplayPreferences.' .. accountId, tostring(gameplayPrefs))
redis.call('JSON.SET',       KEYS[1], '$.PlayerAutoPartyPreferences.' .. accountId, tostring(autoParty))
redis.call('JSON.SET',       KEYS[1], '$.Platforms.' .. accountId, '"' .. platform .. '"')
return redis.call('JSON.GET', KEYS[1])
`;

// ─── Public API ───────────────────────────────────────────────────────────────

export async function getArenaState(lobbyId: string): Promise<ArenaData | null> {
  const state = await redisClient.json.get(arenaStateKey(lobbyId));
  return (state ?? null) as ArenaData | null;
}

export async function createArenaLobby(accountId: string): Promise<ArenaLobby> {
  const base = await createBaseLobby(accountId, "arena_lobby");

  const arenaLobby: ArenaLobby = {
    ...base,
    Teams: [
      { TeamIndex: 0, Players: base.Teams[0].Players, Length: 1 },
      { TeamIndex: 1, Players: {}, Length: 0 },
      { TeamIndex: 2, Players: {}, Length: 0 },
      { TeamIndex: 3, Players: {}, Length: 0 },
      { TeamIndex: 4, Players: {}, Length: 0 },
      { TeamIndex: 5, Players: {}, Length: 0 },
      { TeamIndex: 6, Players: {}, Length: 0 },
      { TeamIndex: 7, Players: {}, Length: 0 },
    ],
  };

  await notifyLobbyJoined(arenaLobby);
  logger.info(`Arena lobby created by ${accountId} - matchId: ${arenaLobby.MatchID}`);
  return arenaLobby;
}

export async function joinArenaLobby(
  matchId: string,
  accountId: string,
  prefs: {
    AutoPartyPreference: boolean;
    CrossplayPreference: number;
    GameplayPreferences: number;
    Platform: string;
    HissCrc: number;
    Version: string;
  },
): Promise<ArenaLobby | null> {
  const result = await evalLua(
    LUA_JOIN_ARENA_LOBBY,
    [lobbyKey(matchId)],
    [
      accountId,
      new Date().toISOString(),
      prefs.GameplayPreferences.toString(),
      prefs.AutoPartyPreference.toString(),
      prefs.CrossplayPreference.toString(),
      prefs.Platform,
    ],
  );

  if (!result) return null;
  const raw = result as string;
  if (raw === "false") return null;

  const updatedLobby = JSON.parse(raw) as ArenaLobby;

  await redisClient.publish(
    LOBBY_JOINED_CHANNEL,
    JSON.stringify({ lobbyId: matchId, accountId } satisfies LobbyCreatedMessage),
  );

  const playerTeam = updatedLobby.Teams.find((t) => t.Players[accountId]);
  const otherPlayers = Object.keys(updatedLobby.PlayerGameplayPreferences).filter(
    (id) => id !== accountId,
  );

  if (otherPlayers.length > 0) {
    await broadcastNotificationToUsers({
      exclude: [accountId],
      users: otherPlayers,
      data: {
        data: {
          MatchID: matchId,
          template_id: "PlayerJoinedLobby",
          Player: playerTeam?.Players[accountId],
          TeamIndex: playerTeam?.TeamIndex,
        },
        payload: { custom_notification: "realtime", match: { id: matchId } },
        cmd: "update",
        header: "",
      },
    });
  }

  return updatedLobby;
}

// ─── Arena assembly ───────────────────────────────────────────────────────────

/**
 * Assemble an arena match from a single ArenaLobby.
 * The lobby's Teams represent the real-player teams already assembled by the
 * caller (route for a manual start, or the matchmaking worker for queued play).
 * Any missing teams / players are padded with bots.
 *
 * Returns the arenaId string on success, or null on failure.
 */
export async function assembleArenaMatch(lobby: ArenaLobby): Promise<string | null> {
  const arenaId = new ObjectId().toHexString();
  const arenaConstants = getArenaConstants();

  // ── 1. Extract real-player teams from the lobby, preserving pairs ─────────
  const rawTeams: string[][] = [];
  for (const team of lobby.Teams) {
    const realPlayers = Object.entries(team.Players)
      .filter(([, p]) => p.BotSettingSlug === "")
      .map(([id]) => id);
    if (realPlayers.length > 0) {
      rawTeams.push(realPlayers);
    }
  }

  // ── 2. Ensure at least 4 real teams; pad with bot-only teams to reach 8 ──
  const MIN_REAL_TEAMS = 4;
  const botTeamsNeeded = Math.max(
    TOTAL_TEAMS - rawTeams.length,
    rawTeams.length < MIN_REAL_TEAMS ? MIN_REAL_TEAMS : 0,
  );
  const totalTeams = rawTeams.length + botTeamsNeeded;
  const targetTeams = Math.max(totalTeams, TOTAL_TEAMS);

  // Pad rawTeams to targetTeams with empty bot-only slots
  while (rawTeams.length < targetTeams) {
    rawTeams.push([]);
  }
  // Cap to TOTAL_TEAMS
  const teamSlots = rawTeams.slice(0, TOTAL_TEAMS);

  // ── 3. Generate a new ObjectId for each team ──────────────────────────────
  const teamIds = teamSlots.map(() => new ObjectId().toHexString());

  // ── 4. Fill each team to PLAYERS_PER_TEAM with bots ──────────────────────
  const teams: { teamId: string; players: string[] }[] = teamSlots.map((players, i) => {
    const filled = [...players];
    while (filled.length < PLAYERS_PER_TEAM) {
      filled.push(generateBotId());
    }
    return { teamId: teamIds[i], players: filled };
  });

  // ── 5. Fetch PlayerConfig for all players (real via Redis, bots generated) ─
  const playerConfigMap = new Map<string, PlayerConfig>();
  for (const { players } of teams) {
    for (const pid of players) {
      if (!playerConfigMap.has(pid)) {
        const config = await getPlayerConfig(pid);
        if (config) playerConfigMap.set(pid, config);
      }
    }
  }

  const realPlayerIds = teams.flatMap(({ players }) => players.filter((p) => !isBotId(p)));

  // ── 6. Build PlayerInfo ───────────────────────────────────────────────────
  const playerInfo: Record<string, ArenaPlayerInfo> = {};
  for (const { teamId, players } of teams) {
    for (const pid of players) {
      const config = playerConfigMap.get(pid)!;
      const isBot = config.bIsBot;
      playerInfo[pid] = {
        Loadout: { Character: "", Skin: "" },
        SelectableCharacters: isBot ? [] : await getRandomCharacters(SELECTABLE_CHARACTER_COUNT),
        AccountId: pid,
        GameplayPreferences: config.GameplayPreferences,
        TeamId: teamId,
        PlayerData: createArenaPlayerData(),
        CharacterClass: 0,
        AccountInfo: {
          RingoutVfx: config.RingoutVfx,
          Taunts: config.Taunts,
          Banner: config.Banner,
          ProfileIcon: config.ProfileIcon,
          Name: isBot ? (config.Username as string) : "",
        },
        bIsBot: isBot,
        ...(isBot
          ? {}
          : { Stats: createArenaPlayerStats(), CurrentShop: [], CurrentShopLocal: null }),
      };
    }
  }

  // ── 7. Build TeamInfo with round-1 pairings (0v1, 2v3, 4v5, 6v7) ─────────
  const roundMap = getRandomMap2v2();

  const teamInfo: Record<string, ArenaTeamInfo> = {};
  for (let i = 0; i < TOTAL_TEAMS; i++) {
    const { teamId, players } = teams[i];
    const opponentIdx = i % 2 === 0 ? i + 1 : i - 1;
    const opponentId = teamIds[opponentIdx];

    const hasPlayedTeam: Record<string, boolean> = {};
    for (let j = 0; j < TOTAL_TEAMS; j++) {
      if (j !== i) hasPlayedTeam[teamIds[j]] = j === opponentIdx;
    }

    teamInfo[teamId] = {
      TeamId: teamId,
      TeamIndex: i,
      Players: players,
      Stats: createArenaTeamStats(),
      LifeRemaining: 100,
      FinalRank: 0,
      RoundOpponents: [opponentId],
      HasPlayedTeam: hasPlayedTeam,
      Matches: [],
      PriorMap: roundMap,
    };
  }

  const arenaData: ArenaData = {
    AllMultiplayParams: lobby.AllMultiplayParams,
    PlayerInfo: playerInfo,
    CurrentRound: 1,
    TeamInfo: teamInfo,
    Players: realPlayerIds,
  };

  // ── 8. Two-pass match assembly ────────────────────────────────────────────
  //
  // Pass A: assign every matchId to TeamInfo.Matches FIRST (for all 4 pairs,
  // including Sim matches for bot-only pairs). This ensures arenaData is
  // fully populated before any WS notification is serialised in Pass B.

  type PairInfo = {
    matchId: string;
    isRealMatch: boolean;
    matchPlayers: Record<string, PlayerConfig>;
    map: string;
  };
  const pairs: PairInfo[] = [];

  for (let i = 0; i < TOTAL_TEAMS; i += 2) {
    const { teamId: teamAId, players: teamAPlayers } = teams[i];
    const { teamId: teamBId, players: teamBPlayers } = teams[i + 1];

    const realInMatch = [...teamAPlayers, ...teamBPlayers].filter((pid) => !isBotId(pid));
    const isRealMatch = realInMatch.length > 0;
    const matchId = isRealMatch ? new ObjectId().toHexString() : `Sim${randomUUID()}`;

    // Assign match IDs now — before any notification is sent
    arenaData.TeamInfo[teamAId].Matches.push(matchId);
    arenaData.TeamInfo[teamBId].Matches.push(matchId);

    // Build match Players for this 2v2
    const matchPlayers: Record<string, PlayerConfig> = {};
    for (let j = 0; j < teamAPlayers.length; j++) {
      const pid = teamAPlayers[j];
      const playerConfig = playerConfigMap.get(pid)!;
      if (playerConfig.bIsBot) {
        continue;
      }
      matchPlayers[pid] = {
        ...playerConfig,
        Username: {},
        PlayerIndex: j,
        TeamIndex: 0,
        Character: "",
        Skin: "",
        Buffs: [],
        Handicap: 0,
        PartyId: null,
        PartyMember: null,
        IsHost: false,
      };
    }
    for (let j = 0; j < teamBPlayers.length; j++) {
      const pid = teamBPlayers[j];
      const playerConfig = playerConfigMap.get(pid)!;
      if (playerConfig.bIsBot) {
        continue;
      }
      matchPlayers[pid] = {
        ...playerConfig,
        Username: {},
        PlayerIndex: j,
        TeamIndex: 1,
        Character: "",
        Skin: "",
        Buffs: [],
        Handicap: 0,
        PartyId: null,
        PartyMember: null,
        IsHost: false,
      };
    }

    pairs.push({ matchId, isRealMatch, matchPlayers, map: roundMap });
  }

  // Pass B: now that arenaData.TeamInfo.Matches is fully populated for every
  // team, build and publish GameplayConfig for each real match pair.
  for (const { matchId, isRealMatch, matchPlayers, map } of pairs) {
    if (!isRealMatch) continue;

    const gameplayConfig: GameplayConfig = {
      ArenaId: arenaId,
      ArenaData: arenaData,
      ArenaConstants: arenaConstants,
      GameplayConfig: {
        ArenaModeInfo: {
          FaceoffWaitTime: arenaConstants.FaceoffWaitTime,
          bReadyToStart: false,
          ShopTime: arenaConstants.CharacterSelectTime,
        },
        RiftNodeId: "",
        ScoreEvaluationRule: "TargetScoreIsWin",
        bIsPvP: true,
        ScoreAttributionRule: "AttributeToAttacker",
        MatchDurationSeconds: arenaConstants.RoundLength,
        Created: new Date(),
        EventQueueSlug: "",
        bModeGrantsProgress: true,
        TeamData: [],
        Spectators: {},
        bIsRanked: false,
        bIsCustomGame: false,
        Players: matchPlayers,
        CustomGameSettings: {
          bHazardsEnabled: true,
          bShieldsEnabled: true,
          MatchTime: 420,
          NumRingouts: -1,
        },
        HudSettings: { bDisplayPortraits: true, bDisplayTimer: true, bDisplayStocks: true },
        bIsCasualSpecial: false,
        bAllowMapHazards: true,
        RiftNodeAttunement: "Attunements:Any",
        CountdownDisplay: "CountdownTypes:ArenaRound",
        Cluster: "ec2-us-east-1-dokken",
        WorldBuffs: [],
        bIsTutorial: false,
        MatchId: matchId,
        bIsOnlineMatch: true,
        ModeString: "arena",
        Map: map,
        bIsRift: false,
      },
    };

    await notifyActiveMatchCreated(gameplayConfig);
  }

  // ── 9. Persist ArenaData ──────────────────────────────────────────────────
  const arenaDataKey = arenaStateKey(arenaId);
  await redisClient
    .multi()
    .json.set(arenaDataKey, "$", arenaData as Parameters<typeof redisClient.json.set>[2])
    .expire(arenaDataKey, ARENA_EX)
    .exec();
  return arenaId;
}

// ─── Per-round actions ────────────────────────────────────────────────────────

// Returns: "already_set" | "all_selected" | "ok" | "not_found"
// Atomically sets character/skin/class on the player and checks if all real players are done.
const LUA_SELECT_CHARACTER = `
local s = redis.call('JSON.GET', KEYS[1])
if not s then return 'not_found' end
local data = cjson.decode(s)

local accountId   = ARGV[1]
local character   = ARGV[2]
local skin        = ARGV[3]
local charClass   = tonumber(ARGV[4])
local bRandom     = ARGV[5] == 'true'

local player = data.PlayerInfo[accountId]
if not player then return 'not_found' end

-- Idempotency: if already set, do nothing
if player.Loadout.Character ~= '' then return 'already_set' end

player.Loadout.Character      = character
player.Loadout.Skin           = skin
player.CharacterClass         = charClass
player.SelectableCharacters   = {}
if bRandom and player.Stats then
  player.Stats.bRandomCharacter = true
end

-- Check if all real (non-bot) players have now selected
local allSelected = true
for _, p in pairs(data.PlayerInfo) do
  if not p.bIsBot and p.Loadout.Character == '' then
    allSelected = false
    break
  end
end

redis.call('JSON.SET', KEYS[1], '$', cjson.encode(data))

if allSelected then return 'all_selected' end
return 'ok'
`;

export async function arenaSelectCharacter(
  arenaLobbyId: string,
  accountId: string,
  characterSlug: string,
  skinSlug: string,
) {
  const stateKey = arenaStateKey(arenaLobbyId);

  // Resolve character/skin/class before the atomic write
  // (random selection needs the player's SelectableCharacters pool)
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return {};

  const playerInfo = arenaData.PlayerInfo[accountId];
  if (!playerInfo) return {};

  // Already selected — nothing to do
  if (playerInfo.Loadout.Character !== "") return {};

  let character = characterSlug;
  let bRandomCharacter = false;
  if (!character) {
    const pool = playerInfo.SelectableCharacters;
    if (pool.length > 0) {
      character = pool[Math.floor(Math.random() * pool.length)];
      bRandomCharacter = true;
    }
  }

  let skin = skinSlug;
  if (!skin && character) {
    skin = getRandomSkinForCharacter(character);
  }

  const charClass = getCharacterClass(character);
  const result = await evalLua(
    LUA_SELECT_CHARACTER,
    [stateKey],
    [accountId, character, skin, String(charClass), String(bRandomCharacter)],
  );

  if (result === "all_selected") {
    console.log(`All characters selected for arena ${arenaLobbyId}, notifying matches...`);
    onAllCharactersSelected(arenaLobbyId).catch((err) =>
      logger.error(`onAllCharactersSelected error: ${err}`),
    );
  }

  return {};
}

export async function arenaSelectCharacterAbsent(arenaLobbyId: string) {
  const arenaData = await getArenaState(arenaLobbyId);
  if (!arenaData) return { body: {}, metadata: null, return_code: 0 };
  return {
    body: { ArenaData: arenaData, ArenaId: arenaLobbyId },
    metadata: null,
    return_code: 20,
  };
}

export async function arenaPlayerShopClosed(
  arenaLobbyId: string,
  _arenaRound: number,
  accountId: string,
  shopDetails: {
    ItemTransactions: { ItemIndex: number; LocalShopIndex: number; bPurchase: boolean }[];
    NumRerolls: number;
  },
) {
  const stateKey = arenaStateKey(arenaLobbyId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return {};

  const playerInfo = arenaData.PlayerInfo[accountId];
  if (!playerInfo || playerInfo.bIsBot) return {};

  // Already submitted — ignore duplicate calls
  if (playerInfo.CurrentShopLocal !== null && (playerInfo.CurrentShopLocal?.length ?? -1) === 0) {
    return {};
  }

  const playerData = playerInfo.PlayerData;
  const stats = playerInfo.Stats!;
  const shopLocal = playerInfo.CurrentShopLocal ?? [];
  const constants = getArenaConstants();
  const itemXpPerLevel = constants.ItemXpPerLevel; // [0, 1, 3]

  let currencySpent = 0;
  let itemsPurchased = 0;
  let itemsSold = 0;
  let itemsLeveled = 0;

  // Process reroll cost
  const freeRerolls = playerData.FreeShopRerolls;
  const rerollCost = Math.max(0, shopDetails.NumRerolls - freeRerolls) * playerData.ShopRerollCost;
  playerData.CurrencyAmount -= rerollCost;
  stats.ShopRerolls += shopDetails.NumRerolls;

  for (const tx of shopDetails.ItemTransactions) {
    if (tx.bPurchase) {
      // Buy: find shop item
      const option = shopLocal[tx.LocalShopIndex];
      if (!option) continue;
      const shopItem = option.CurrentShop?.[tx.ItemIndex];
      if (!shopItem) continue;

      const cost = shopItem.Cost;
      if (playerData.CurrencyAmount < cost) continue;

      playerData.CurrencyAmount -= cost;
      currencySpent += cost;
      itemsPurchased++;

      // Find empty slot or existing slot with same slug (stack)
      const slug = shopItem.Slug;
      const existingSlot = playerData.Inventory.find(
        (slot) => slot.Slug === slug && slot.Level < itemXpPerLevel.length,
      );

      if (existingSlot) {
        existingSlot.Xp += 1;
        existingSlot.SellValue += cost - 1;
        // Level up while Xp >= NextLevelXp
        while (
          existingSlot.Level < itemXpPerLevel.length - 1 &&
          existingSlot.Xp >= existingSlot.NextLevelXp
        ) {
          existingSlot.Xp -= existingSlot.NextLevelXp;
          existingSlot.Level += 1;
          existingSlot.NextLevelXp = itemXpPerLevel[existingSlot.Level] ?? 0;
          itemsLeveled++;
        }
      } else {
        // Find empty slot
        const emptySlot = playerData.Inventory.find((slot) => slot.Slug === "");
        if (emptySlot) {
          emptySlot.Slug = slug;
          emptySlot.Level = 1;
          emptySlot.Xp = 0;
          emptySlot.NextLevelXp = itemXpPerLevel[1] ?? 1;
          emptySlot.SellValue = cost - 1;
        }
      }
    } else {
      // Sell: clear inventory slot at ItemIndex
      const slot = playerData.Inventory[tx.ItemIndex];
      if (!slot || slot.Slug === "") continue;

      playerData.CurrencyAmount += slot.SellValue;
      slot.Slug = "";
      slot.Level = 0;
      slot.Xp = 0;
      slot.NextLevelXp = 0;
      slot.SellValue = 0;
      itemsSold++;
    }
  }

  // Update interest boost if arenaitem_interestboost is in inventory
  const interestBoostSlot = playerData.Inventory.find(
    (slot) => slot.Slug === "arenaitem_interestboost",
  );
  if (interestBoostSlot) {
    const interestPerBoost = constants.InterestPerBoost;
    playerData.InterestPer =
      interestPerBoost[interestBoostSlot.Level - 1] ?? playerData.InterestPer;
  }

  // Update stats
  stats.ItemsPurchased += itemsPurchased;
  stats.ItemsSold += itemsSold;
  stats.ItemsLeveled += itemsLeveled;
  stats.CurrencySpent += currencySpent;

  // Mark player done: clear CurrentShopLocal
  arenaData.PlayerInfo[accountId].PlayerData = playerData;
  arenaData.PlayerInfo[accountId].Stats = stats;
  arenaData.PlayerInfo[accountId].CurrentShopLocal = [];

  // Persist
  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );

  // Check if all real players are done (CurrentShopLocal empty)
  const refreshed = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!refreshed) return {};

  const allDone = Object.values(refreshed.PlayerInfo)
    .filter((p) => !p.bIsBot)
    .every((p) => p.CurrentShopLocal !== null && (p.CurrentShopLocal?.length ?? -1) === 0);

  if (allDone) {
    notifyMatchReadyToStart(arenaLobbyId).catch((err) =>
      logger.error(`notifyMatchReadyToStart error: ${err}`),
    );
  }

  return {};
}

export async function arenaCheckin(
  _arenaParentId: string,
  _arenaRound: number,
  _containerMatchId: string,
  _accountId: string,
) {
  return {};
}

export async function arenaRerollCharacters(arenaLobbyId: string, accountId: string) {
  const arenaKey = arenaStateKey(arenaLobbyId);
  const arenaData = (await redisClient.json.get(arenaKey)) as ArenaData | null;
  if (!arenaData) return null;

  const player = arenaData.PlayerInfo[accountId];
  if (!player) return null;

  const rerollCost = getArenaConstants().ShopCharacterRerollCost;

  if (player.PlayerData.CurrencyAmount < rerollCost) {
    return {
      Result: {
        BaseResponse: {
          bSuccess: false,
          PlayerId: accountId,
          FailureReason: "Insufficient currency",
        },
        SelectableCharacters: player.SelectableCharacters,
        PlayerData: player.PlayerData,
      },
    };
  }

  const newCurrency = player.PlayerData.CurrencyAmount - rerollCost;
  const newCharacters = await getRandomCharacters(SELECTABLE_CHARACTER_COUNT);

  await Promise.all([
    redisClient.json.set(
      arenaKey,
      `$.PlayerInfo.${accountId}.PlayerData.CurrencyAmount`,
      newCurrency,
    ),
    redisClient.json.set(arenaKey, `$.PlayerInfo.${accountId}.SelectableCharacters`, newCharacters),
  ]);

  return {
    Result: {
      BaseResponse: { bSuccess: true, PlayerId: accountId, FailureReason: "" },
      SelectableCharacters: newCharacters,
      PlayerData: { ...player.PlayerData, CurrencyAmount: newCurrency },
    },
  };
}
