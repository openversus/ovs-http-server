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
import {
  generateShopOptions,
  generateItemPool,
  pickItemFromPool,
  getRarityWeightsForRound,
  getGemName,
} from "../../data/arenaItemDefs";
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
    RoundLength: 15,
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

  // ── Assign bots random char/skin (no duplicate characters on same team) ──
  // Track used characters per team so teammates don't share a character
  const usedCharsPerTeam: Record<string, Set<string>> = {};
  // First pass: record real players' characters
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (info.bIsBot) continue;
    arenaData.PlayerInfo[pid].SelectableCharacters = [];
    if (info.Loadout.Character) {
      if (!usedCharsPerTeam[info.TeamId]) usedCharsPerTeam[info.TeamId] = new Set();
      usedCharsPerTeam[info.TeamId].add(info.Loadout.Character);
    }
  }
  // Second pass: assign bots avoiding team duplicates
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (!info.bIsBot) continue;
    if (!usedCharsPerTeam[info.TeamId]) usedCharsPerTeam[info.TeamId] = new Set();
    const teamUsed = usedCharsPerTeam[info.TeamId];

    // Get a pool of characters, pick one not already on the team
    const chars = await getRandomCharacters(SELECTABLE_CHARACTER_COUNT);
    const available = chars.filter((c) => !teamUsed.has(c));
    const character = available.length > 0 ? available[0] : (chars[0] ?? "");
    teamUsed.add(character);

    const skin = character ? getRandomSkinForCharacter(character) : "";
    const charClass = getCharacterClass(character);
    arenaData.PlayerInfo[pid].Loadout = { Character: character, Skin: skin };
    arenaData.PlayerInfo[pid].CharacterClass = charClass;
    arenaData.PlayerInfo[pid].SelectableCharacters = [];
  }

  // ── Generate shops for each real player ───────────────────────────────────
  const constants = getArenaConstants();
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (info.bIsBot) continue;
    const shopOptions = generateShopOptions(
      arenaData.ItemPool,
      constants.ShopLevelWeights,
      arenaData.CurrentRound,
    );
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
      const gameplayConfig = activeMatch.GameplayConfig.GameplayConfig;

      // Build updated Players map: overlay character/skin from arenaData
      for (const pid of Object.keys(gameplayConfig.Players)) {
        gameplayConfig.Players[pid].Skin = arenaData.PlayerInfo[pid].Loadout.Skin;
        gameplayConfig.Players[pid].Character = arenaData.PlayerInfo[pid].Loadout.Character;
      }

      await redisClient.json.set(
        `match:${matchId}`,
        "$",
        activeMatch as Parameters<typeof redisClient.json.set>[2],
      );

      // Collect all ArenaPlayerInfo for players in this match
      const matchPlayerIds = Object.keys(gameplayConfig.Players);

      const notification: RealtimeNotificationUsersMessage = {
        exclude: [],
        users: matchPlayerIds,
        data: {
          data: {
            ArenaId: arenaId,
            MatchId: matchId,
            template_id: "ArenaInventoryLockedNotification",
            GameplayConfig: gameplayConfig,
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
      await broadcastNotificationToUsers(notification);
    }
  }
}

async function notifyMatchReadyToStart(arenaId: string): Promise<void> {
  const stateKey = arenaStateKey(arenaId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return;

  // ── Assign bots items from shared pool using rarity weights ────────────────
  const constants = getArenaConstants();
  const rarities = getRarityWeightsForRound(constants.ShopLevelWeights, arenaData.CurrentRound);
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (!info.bIsBot) continue;
    const buildType = getBotBuildType(info.CharacterClass);
    const inventory = info.PlayerData.Inventory.map((slot) => {
      const item = pickItemFromPool(arenaData.ItemPool, rarities);
      if (!item) return slot; // keep empty slot if pool exhausted
      return {
        Slug: item.slug,
        Level: 1,
        Xp: 0,
        NextLevelXp: 1,
        SellValue: item.cost - 1,
      };
    });
    arenaData.PlayerInfo[pid].PlayerData.Inventory = inventory;
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

      const notification: RealtimeNotificationUsersMessage = {
        exclude: [],
        users: matchPlayerIds,
        data: {
          data: {
            ArenaId: arenaId,
            MatchId: matchId,
            template_id: "ArenaInventoryLockedNotification",
            GameplayConfig: {
              ...activeMatch.GameplayConfig.GameplayConfig,
              Players: updatedPlayers,
              ArenaModeInfo: {
                ...activeMatch.GameplayConfig.GameplayConfig.ArenaModeInfo,
                bReadyToStart: true,
              },
            },
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
        ...(isBot ? {} : { Stats: createArenaPlayerStats(), CurrentShop: [] }),
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
    ItemPool: generateItemPool(arenaConstants.ItemAmountsByRarity),
  };

  // ── 8. Two-pass match assembly ────────────────────────────────────────────
  //
  // Pass A: assign every matchId to TeamInfo.Matches FIRST (for all 4 pairs,
  // including Sim matches for bot-only pairs). This ensures arenaData is
  // fully populated before any WS notification is serialised in Pass B.

  type PairInfo = {
    matchId: string;
    isRealMatch: boolean;
    teamAId: string;
    teamBId: string;
  };
  const pairs: PairInfo[] = [];

  for (let i = 0; i < TOTAL_TEAMS; i += 2) {
    const { teamId: teamAId, players: teamAPlayers } = teams[i];
    const { teamId: teamBId, players: teamBPlayers } = teams[i + 1];

    const realInMatch = [...teamAPlayers, ...teamBPlayers].filter((pid) => !isBotId(pid));
    const isRealMatch = realInMatch.length > 0;
    const matchId = isRealMatch ? new ObjectId().toHexString() : `Sim${randomUUID()}`;

    arenaData.TeamInfo[teamAId].Matches.push(matchId);
    arenaData.TeamInfo[teamBId].Matches.push(matchId);

    pairs.push({ matchId, isRealMatch, teamAId, teamBId });
  }

  // Pass B: build and publish GameplayConfig for each real match pair.
  for (const { matchId, isRealMatch, teamAId, teamBId } of pairs) {
    if (!isRealMatch) continue;

    const gameplayConfig = await buildMatchGameplayConfig(
      arenaId,
      matchId,
      arenaData,
      teamAId,
      teamBId,
      roundMap,
      arenaConstants,
      true,
    );

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
  // "done" = CurrentShopLocal is an empty array; "not started" = undefined
  if (Array.isArray(playerInfo.CurrentShopLocal) && playerInfo.CurrentShopLocal.length === 0) {
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
  currencySpent += rerollCost;
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

      // Item was already removed from pool during shop generation
      const slug = shopItem.Item.Slug;

      // Find empty slot or existing slot with same slug (stack)
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

      // Return item to shared pool
      arenaData.ItemPool[slot.Slug] = (arenaData.ItemPool[slot.Slug] ?? 0) + 1;

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

  // Return unpurchased shop items back to the shared pool
  // (they were reserved from pool during shop generation)
  const purchasedKeys = new Set(
    shopDetails.ItemTransactions
      .filter((tx) => tx.bPurchase)
      .map((tx) => `${tx.LocalShopIndex}:${tx.ItemIndex}`),
  );
  for (let si = 0; si < shopLocal.length; si++) {
    const option = shopLocal[si];
    if (!option?.CurrentShop) continue;
    for (let ii = 0; ii < option.CurrentShop.length; ii++) {
      if (purchasedKeys.has(`${si}:${ii}`)) continue;
      const itemSlug = option.CurrentShop[ii].Item.Slug;
      if (itemSlug) {
        arenaData.ItemPool[itemSlug] = (arenaData.ItemPool[itemSlug] ?? 0) + 1;
      }
    }
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
    .every((p) => Array.isArray(p.CurrentShopLocal) && p.CurrentShopLocal.length === 0);

  if (allDone) {
    notifyMatchReadyToStart(arenaLobbyId).catch((err) =>
      logger.error(`notifyMatchReadyToStart error: ${err}`),
    );
  }

  return {};
}

// ─── End-of-match stat submission (arena) ────────────────────────────────────

export async function submitArenaMatchStats(
  containerMatchId: string,
  endOfMatchStats: {
    PlayerMissionUpdates: Record<string, Record<string, number>>;
    Score: number[];
    WinningTeamIndex: number;
  },
  matchLength: number,
) {
  // Find which arenaId owns this match
  const activeMatch = await getActiveMatch(containerMatchId);
  if (!activeMatch) return;
  const arenaId = activeMatch.GameplayConfig.ArenaId;
  if (!arenaId) return;

  const stateKey = arenaStateKey(arenaId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return;

  const matchPlayers = activeMatch.GameplayConfig.GameplayConfig.Players;
  const { PlayerMissionUpdates, Score, WinningTeamIndex } = endOfMatchStats;

  // ── Map stat keys to MatchStats fields ──────────────────────────────────
  const statMap: Record<string, keyof MatchStats> = {
    "Stat:Game:Character:TotalRingouts": "Ringouts",
    "Stat:Game:Character:TotalDamageDealt": "Damage",
    "Stat:Game:Character:TotalDamageAdded": "DamagedAdded",
    "Stat:Game:Character:TotalDamageMitigated": "DamageMitigated",
    "Stat:Game:Character:TotalKnockbackAdded": "KnockbackAdded",
    "Stat:Game:Character:TotalKnockbackMitigated": "KnockbackMitigated",
    "Stat:Game:Character:TotalHealingReceived": "HealingReceived",
    "Stat:Game:Character:TotalGreyHealthReceived": "GreyHealthReceived",
  };

  // ── Update per-player Stats.MatchStats ──────────────────────────────────
  for (const [pid, rawStats] of Object.entries(PlayerMissionUpdates)) {
    const info = arenaData.PlayerInfo[pid];
    if (!info?.Stats) continue;
    for (const [key, field] of Object.entries(statMap)) {
      if (rawStats[key] !== undefined) {
        info.Stats.MatchStats[field] += rawStats[key];
      }
    }
  }

  // ── Determine which two teams fought in this match ──────────────────────
  const teamIds = new Set<string>();
  for (const pid of Object.keys(matchPlayers)) {
    const info = arenaData.PlayerInfo[pid];
    if (info) teamIds.add(info.TeamId);
  }
  const teamIdArr = [...teamIds];
  if (teamIdArr.length !== 2) {
    // Unexpected — persist what we have and bail
    await redisClient.json.set(
      stateKey,
      "$",
      arenaData as Parameters<typeof redisClient.json.set>[2],
    );
    return;
  }

  // Map WinningTeamIndex (0/1 from the match) to the actual arena teamId.
  // Team 0 in the match is the first team, team 1 is the second.
  // We need to figure out which teamId corresponds to WinningTeamIndex.
  // TeamIndex 0 in the match = first team players, TeamIndex 1 = second team players.
  const teamByMatchIdx: [string, string] = [teamIdArr[0], teamIdArr[1]];
  // Verify by checking a player's TeamIndex in the match
  for (const [pid, playerCfg] of Object.entries(matchPlayers)) {
    const info = arenaData.PlayerInfo[pid];
    if (info) {
      teamByMatchIdx[playerCfg.TeamIndex] = info.TeamId;
    }
  }

  const winTeamId = teamByMatchIdx[WinningTeamIndex];
  const loseTeamId = teamByMatchIdx[WinningTeamIndex === 0 ? 1 : 0];

  // ── Update team stats ───────────────────────────────────────────────────
  const winTeam = arenaData.TeamInfo[winTeamId];
  const loseTeam = arenaData.TeamInfo[loseTeamId];

  if (winTeam) {
    winTeam.Stats.Wins += 1;
    winTeam.Stats.WinStreak += 1;
    winTeam.Stats.LoseStreak = 0;
  }
  if (loseTeam) {
    loseTeam.Stats.Losses += 1;
    loseTeam.Stats.LoseStreak += 1;
    loseTeam.Stats.WinStreak = 0;

    // Reduce LifeRemaining for the losing team based on round
    const constants = getArenaConstants();
    const round = arenaData.CurrentRound;
    const healthIdx = Math.min(round - 1, constants.HealthRoundValues.length - 1);
    const damage = constants.HealthRoundValues[healthIdx];
    loseTeam.LifeRemaining = Math.max(0, loseTeam.LifeRemaining - damage);
  }

  // Aggregate MatchStats per team from their players
  for (const tid of teamIdArr) {
    const team = arenaData.TeamInfo[tid];
    if (!team) continue;
    // Reset team match stats for this round (accumulate from players)
    const teamMatchStats = createMatchStats();
    for (const pid of team.Players) {
      const info = arenaData.PlayerInfo[pid];
      if (!info?.Stats) continue;
      for (const key of Object.keys(teamMatchStats) as (keyof MatchStats)[]) {
        teamMatchStats[key] += info.Stats.MatchStats[key];
      }
    }
    team.Stats.MatchStats = teamMatchStats;
  }

  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );
}

// ─── Reusable match-pairing helpers ──────────────────────────────────────────

/**
 * Pair teams for a new round. Returns array of [teamAId, teamBId] pairs.
 * Tries to avoid rematches by checking HasPlayedTeam.
 * Only pairs teams that are still alive (LifeRemaining > 0).
 */
function pairTeamsForRound(arenaData: ArenaData): [string, string][] {
  const aliveTeamIds = Object.values(arenaData.TeamInfo)
    .filter((t) => t.LifeRemaining > 0)
    .map((t) => t.TeamId);

  // Sort by TeamIndex for consistency
  aliveTeamIds.sort((a, b) => arenaData.TeamInfo[a].TeamIndex - arenaData.TeamInfo[b].TeamIndex);

  const paired = new Set<string>();
  const pairs: [string, string][] = [];

  // First pass: try to pair with teams not yet faced
  for (const teamId of aliveTeamIds) {
    if (paired.has(teamId)) continue;
    const team = arenaData.TeamInfo[teamId];
    const opponent = aliveTeamIds.find(
      (otherId) => otherId !== teamId && !paired.has(otherId) && !team.HasPlayedTeam[otherId],
    );
    if (opponent) {
      pairs.push([teamId, opponent]);
      paired.add(teamId);
      paired.add(opponent);
    }
  }

  // Second pass: pair remaining with anyone available
  for (const teamId of aliveTeamIds) {
    if (paired.has(teamId)) continue;
    const opponent = aliveTeamIds.find((otherId) => otherId !== teamId && !paired.has(otherId));
    if (opponent) {
      pairs.push([teamId, opponent]);
      paired.add(teamId);
      paired.add(opponent);
    }
  }

  return pairs;
}

/**
 * Build a GameplayConfig for a 2v2 match between two arena teams.
 * Reusable for both initial assembly and next-round match creation.
 */
async function buildMatchGameplayConfig(
  arenaId: string,
  matchId: string,
  arenaData: ArenaData,
  teamAId: string,
  teamBId: string,
  map: string,
  arenaConstants: ArenaConstants,
  includeConstants: boolean,
): Promise<GameplayConfig> {
  const teamAPlayers = arenaData.TeamInfo[teamAId].Players;
  const teamBPlayers = arenaData.TeamInfo[teamBId].Players;

  const matchPlayers: Record<string, any> = {};

  for (let j = 0; j < teamAPlayers.length; j++) {
    const pid = teamAPlayers[j];
    const config = await getPlayerConfig(pid);
    if (!config) continue;
    const isBot = config.bIsBot;
    const info = arenaData.PlayerInfo[pid];
    matchPlayers[pid] = {
      ...config,
      Username: isBot ? (config.Username as string) : {},
      bUseCharacterDisplayName: false,
      PlayerIndex: j * 2,
      TeamIndex: 0,
      Character: info?.Loadout.Character ?? "",
      Skin: info?.Loadout.Skin ?? "",
      Buffs: [],
      Perks: [],
      Handicap: 0,
      PartyId: null,
      PartyMember: null,
      IsHost: false,
    } as PlayerConfig;
  }

  for (let j = 0; j < teamBPlayers.length; j++) {
    const pid = teamBPlayers[j];
    const config = await getPlayerConfig(pid);
    if (!config) continue;
    const isBot = config.bIsBot;
    const info = arenaData.PlayerInfo[pid];
    matchPlayers[pid] = {
      ...config,
      Username: isBot ? (config.Username as string) : {},
      bUseCharacterDisplayName: false,
      PlayerIndex: j * 2 + 1,
      TeamIndex: 1,
      Character: info?.Loadout.Character ?? "",
      Skin: info?.Loadout.Skin ?? "",
      Buffs: [],
      Perks: [],
      Handicap: 0,
      PartyId: null,
      PartyMember: null,
      IsHost: false,
    } as PlayerConfig;
  }

  const gameplayConfig: GameplayConfig = {
    ArenaId: arenaId,
    ArenaData: arenaData,

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
  if (includeConstants) {
    gameplayConfig.ArenaConstants = arenaConstants;
  }
  return gameplayConfig;
}

// ─── Arena check-in ──────────────────────────────────────────────────────────

/**
 * Per-player checkin: calculate currency, generate shop, mark as checked in.
 * Uses CurrentShopLocal non-empty as "checked in" marker.
 * When all real players have checked in, triggers next round match creation.
 */
export async function arenaCheckin(
  arenaParentId: string,
  arenaRound: number,
  containerMatchId: string,
  accountId: string,
) {
  const stateKey = arenaStateKey(arenaParentId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return {};

  const playerInfo = arenaData.PlayerInfo[accountId];
  if (!playerInfo || playerInfo.bIsBot) return {};

  // Already checked in — CurrentShopLocal is non-empty array
  if (Array.isArray(playerInfo.CurrentShopLocal) && playerInfo.CurrentShopLocal.length > 0) {
    return {};
  }

  const constants = getArenaConstants();
  const round = arenaData.CurrentRound;
  const roundIdx = Math.min(round - 1, constants.CurrencyPerRound.length - 1);
  const baseCurrency = constants.CurrencyPerRound[roundIdx];

  const teamInfo = arenaData.TeamInfo[playerInfo.TeamId];
  if (!teamInfo) return {};

  // ── Calculate currency payout for this player ───────────────────────────
  let payout = baseCurrency;

  // Ringout bonus
  const teamRingouts = teamInfo.Stats.MatchStats.Ringouts;
  const ringoutIdx = Math.min(teamRingouts, constants.CurrencyPerRingout.length - 1);
  payout += constants.CurrencyPerRingout[ringoutIdx];

  // Win/Lose + streak bonuses
  if (teamInfo.Stats.WinStreak > 0) {
    payout += constants.CurrencyForWin;
    const streakIdx = Math.min(teamInfo.Stats.WinStreak, constants.CurrencyForWinStreak.length - 1);
    payout += constants.CurrencyForWinStreak[streakIdx];
  }
  if (teamInfo.Stats.LoseStreak > 0) {
    const streakIdx = Math.min(
      teamInfo.Stats.LoseStreak,
      constants.CurrencyForLoseStreak.length - 1,
    );
    payout += constants.CurrencyForLoseStreak[streakIdx];
  }

  // Interest (calculated on gold BEFORE payout)
  const interest = Math.min(
    Math.floor(playerInfo.PlayerData.CurrencyAmount / playerInfo.PlayerData.InterestPer),
    constants.MaxInterest,
  );
  payout += interest;
  if (playerInfo.Stats) {
    playerInfo.Stats.InterestGained += interest;
  }

  playerInfo.PlayerData.CurrencyAmount += payout;

  // ── Generate new shop for this player ───────────────────────────────────
  const shopOptions = generateShopOptions(arenaData.ItemPool, constants.ShopLevelWeights, round);
  playerInfo.CurrentShopLocal = shopOptions;

  // Persist
  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );
  console.log("arena details",JSON.stringify(arenaData,null,2))

  // ── Check if all real players have now checked in ───────────────────────
  const refreshed = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!refreshed) return {};

  const allCheckedIn = Object.values(refreshed.PlayerInfo)
    .filter((p) => !p.bIsBot)
    .every((p) => Array.isArray(p.CurrentShopLocal) && p.CurrentShopLocal.length > 0);

  if (allCheckedIn) {
    onAllPlayersCheckedIn(arenaParentId).catch((err) =>
      logger.error(`onAllPlayersCheckedIn error: ${err}`),
    );
  }

  return {};
}

async function onAllPlayersCheckedIn(arenaId: string): Promise<void> {
  const stateKey = arenaStateKey(arenaId);
  const arenaData = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!arenaData) return;

  const arenaConstants = getArenaConstants();
  const round = arenaData.CurrentRound;
  const roundIdx = Math.min(round - 1, arenaConstants.CurrencyPerRound.length - 1);
  const baseCurrency = arenaConstants.CurrencyPerRound[roundIdx];

  // ── Update bot currency and items ───────────────────────────────────────
  const rarities = getRarityWeightsForRound(arenaConstants.ShopLevelWeights, round);
  for (const [pid, info] of Object.entries(arenaData.PlayerInfo)) {
    if (!info.bIsBot) continue;

    const teamInfo = arenaData.TeamInfo[info.TeamId];
    if (!teamInfo) continue;

    // Calculate same currency payout as real players
    let payout = baseCurrency;
    const teamRingouts = teamInfo.Stats.MatchStats.Ringouts;
    const ringoutIdx = Math.min(teamRingouts, arenaConstants.CurrencyPerRingout.length - 1);
    payout += arenaConstants.CurrencyPerRingout[ringoutIdx];
    if (teamInfo.Stats.WinStreak > 0) {
      payout += arenaConstants.CurrencyForWin;
      const streakIdx = Math.min(
        teamInfo.Stats.WinStreak,
        arenaConstants.CurrencyForWinStreak.length - 1,
      );
      payout += arenaConstants.CurrencyForWinStreak[streakIdx];
    }
    if (teamInfo.Stats.LoseStreak > 0) {
      const streakIdx = Math.min(
        teamInfo.Stats.LoseStreak,
        arenaConstants.CurrencyForLoseStreak.length - 1,
      );
      payout += arenaConstants.CurrencyForLoseStreak[streakIdx];
    }
    const interest = Math.min(
      Math.floor(info.PlayerData.CurrencyAmount / info.PlayerData.InterestPer),
      arenaConstants.MaxInterest,
    );
    payout += interest;
    info.PlayerData.CurrencyAmount += payout;

    // Return old bot items to pool, then assign new ones
    for (const slot of info.PlayerData.Inventory) {
      if (slot.Slug) {
        arenaData.ItemPool[slot.Slug] = (arenaData.ItemPool[slot.Slug] ?? 0) + 1;
      }
    }
    info.PlayerData.Inventory = info.PlayerData.Inventory.map(() => {
      const item = pickItemFromPool(arenaData.ItemPool, rarities);
      if (!item) return { Slug: "", Level: 0, Xp: 0, NextLevelXp: 0, SellValue: 0 };
      return { Slug: item.slug, Level: 1, Xp: 0, NextLevelXp: 1, SellValue: item.cost - 1 };
    });
    info.BotSettings = { BuildType: getBotBuildType(info.CharacterClass) };
  }

  // Advance round
  arenaData.CurrentRound += 1;

  // ── Pair teams for the new round ────────────────────────────────────────
  const pairs = pairTeamsForRound(arenaData);
  const roundMap = getRandomMap2v2();

  // Clear old match references and update HasPlayedTeam / RoundOpponents
  for (const team of Object.values(arenaData.TeamInfo)) {
    team.Matches = [];
    team.RoundOpponents = [];
    team.PriorMap = roundMap;
  }

  // ── Create matches for each pair ────────────────────────────────────────
  type PairMatch = {
    matchId: string;
    isRealMatch: boolean;
    teamAId: string;
    teamBId: string;
  };
  const pairMatches: PairMatch[] = [];

  for (const [teamAId, teamBId] of pairs) {
    const teamAPlayers = arenaData.TeamInfo[teamAId].Players;
    const teamBPlayers = arenaData.TeamInfo[teamBId].Players;
    const realInMatch = [...teamAPlayers, ...teamBPlayers].filter((pid) => !isBotId(pid));
    const isRealMatch = realInMatch.length > 0;
    const matchId = isRealMatch ? new ObjectId().toHexString() : `Sim${randomUUID()}`;

    // Assign match IDs and update HasPlayedTeam
    arenaData.TeamInfo[teamAId].Matches.push(matchId);
    arenaData.TeamInfo[teamBId].Matches.push(matchId);
    arenaData.TeamInfo[teamAId].RoundOpponents.push(teamBId);
    arenaData.TeamInfo[teamBId].RoundOpponents.push(teamAId);
    arenaData.TeamInfo[teamAId].HasPlayedTeam[teamBId] = true;
    arenaData.TeamInfo[teamBId].HasPlayedTeam[teamAId] = true;

    pairMatches.push({ matchId, isRealMatch, teamAId, teamBId });
  }

  // Persist arenaData before sending notifications (so ArenaData in GameplayConfig is up-to-date)
  await redisClient.json.set(
    stateKey,
    "$",
    arenaData as Parameters<typeof redisClient.json.set>[2],
  );

  // ── Build and publish GameplayConfig for each real match ────────────────
  for (const { matchId, isRealMatch, teamAId, teamBId } of pairMatches) {
    if (!isRealMatch) continue;

    const gameplayConfig = await buildMatchGameplayConfig(
      arenaId,
      matchId,
      arenaData,
      teamAId,
      teamBId,
      roundMap,
      getArenaConstants(),
      false,
    );
    console.log(`Publishing next round match ${matchId}`, JSON.stringify(gameplayConfig));
    await notifyActiveMatchCreated(gameplayConfig, "OnArenaNextMatch");
  }
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
