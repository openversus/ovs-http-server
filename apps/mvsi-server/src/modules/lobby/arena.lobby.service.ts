import { logger } from "@mvsi/logger";
import { redisClient } from "@mvsi/redis";
import { ObjectId } from "mongodb";
import { randomUUID } from "node:crypto";
import { getRandomMap2v2 } from "../../data/maps";
import { getAssetsByType } from "../../loadAssets";
import type { GameplayConfig } from "../matchmaking/matchmaking.types";
import { notifyActiveMatchCreated } from "../matchmaking/matchmaking.service";
import { broadcastNotificationToUsers } from "../notifications/notifications.utils";
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
      .map(() => ({ Xp: 0, Level: 0, Slug: "", NextLevelXp: 0 })),
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

function getRandomCharacters(count: number): string[] {
  const chars = getAssetsByType("CharacterData");
  if (chars.length === 0) return [];
  const shuffled = [...chars].sort(() => Math.random() - 0.5);
  return shuffled.slice(0, Math.min(count, shuffled.length)).map((c) => c.slug);
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
 * Assemble an arena match from one or more ArenaLobbies.
 * Called either from the start_arena_match route (single lobby) or
 * from the matchmaking worker (multiple matched lobbies).
 */
export async function assembleArenaMatch(lobbies: ArenaLobby[]): Promise<ArenaData | null> {
  if (lobbies.length === 0) return null;

  const arenaId = new ObjectId().toHexString();
  const arenaConstants = getArenaConstants();

  // ── 1. Extract real-player teams from all lobbies, preserving pairs ──────
  // A team slot in a lobby with at least 1 real player becomes one arena team.
  const rawTeams: string[][] = [];
  for (const lobby of lobbies) {
    for (const team of lobby.Teams) {
      const realPlayers = Object.entries(team.Players)
        .filter(([, p]) => p.BotSettingSlug === "")
        .map(([id]) => id);
      if (realPlayers.length > 0) {
        rawTeams.push(realPlayers);
      }
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
        SelectableCharacters: isBot ? [] : getRandomCharacters(SELECTABLE_CHARACTER_COUNT),
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
          Name: config.Username as string,
        },
        bIsBot: isBot,
        ...(isBot
          ? {}
          : { Stats: createArenaPlayerStats(), CurrentShop: [], CurrentShopLocal: [] }),
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
    AllMultiplayParams: lobbies[0].AllMultiplayParams,
    PlayerInfo: playerInfo,
    CurrentRound: 1,
    TeamInfo: teamInfo,
    Players: realPlayerIds,
  };

  // ── 8. Create per-match GameplayConfig, store, and notify real matches ─────
  for (let i = 0; i < TOTAL_TEAMS; i += 2) {
    const { teamId: teamAId, players: teamAPlayers } = teams[i];
    const { teamId: teamBId, players: teamBPlayers } = teams[i + 1];

    const realInMatch = [...teamAPlayers, ...teamBPlayers].filter((pid) => !isBotId(pid));
    const isRealMatch = realInMatch.length > 0;
    const matchId = isRealMatch ? new ObjectId().toHexString() : `Sim${randomUUID()}`;

    arenaData.TeamInfo[teamAId].Matches.push(matchId);
    arenaData.TeamInfo[teamBId].Matches.push(matchId);

    // Build match Players for this 2v2
    const matchPlayers: Record<string, PlayerConfig> = {};
    for (let j = 0; j < teamAPlayers.length; j++) {
      const pid = teamAPlayers[j];
      matchPlayers[pid] = {
        ...playerConfigMap.get(pid)!,
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
      matchPlayers[pid] = {
        ...playerConfigMap.get(pid)!,
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
        Map: roundMap,
        bIsRift: false,
      },
    };

    if (isRealMatch) {
      await notifyActiveMatchCreated(gameplayConfig);
    }
  }

  // ── 9. Persist ArenaData ──────────────────────────────────────────────────
  const stateKey = arenaStateKey(arenaId);
  await redisClient
    .multi()
    .json.set(stateKey, "$", arenaData as Parameters<typeof redisClient.json.set>[2])
    .expire(stateKey, ARENA_EX)
    .exec();

  logger.info(
    `Arena match assembled: arenaId=${arenaId}, teams=${TOTAL_TEAMS}, realPlayers=${realPlayerIds.length}`,
  );
  return arenaData;
}

// ─── Per-round actions ────────────────────────────────────────────────────────

export async function arenaSelectCharacter(
  arenaLobbyId: string,
  accountId: string,
  characterSlug: string,
  skinSlug: string,
) {
  const stateKey = arenaStateKey(arenaLobbyId);
  await redisClient.json.set(stateKey, `$.PlayerInfo.${accountId}.Loadout`, {
    Character: characterSlug,
    Skin: skinSlug,
  });
  return {};
}

export async function arenaPlayerShopClosed(
  arenaLobbyId: string,
  _arenaRound: number,
  _accountId: string,
  _shopDetails: {
    ItemTransactions: { ItemIndex: number; LocalShopIndex: number; bPurchase: boolean }[];
    NumRerolls: number;
  },
) {
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
  const stateKey = arenaStateKey(arenaLobbyId);
  const state = (await redisClient.json.get(stateKey)) as ArenaData | null;
  if (!state) return null;

  const player = state.PlayerInfo[accountId];
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
  const newCharacters = getRandomCharacters(SELECTABLE_CHARACTER_COUNT);

  await redisClient.json.set(
    stateKey,
    `$.PlayerInfo.${accountId}.PlayerData.CurrencyAmount`,
    newCurrency,
  );
  await redisClient.json.set(
    stateKey,
    `$.PlayerInfo.${accountId}.SelectableCharacters`,
    newCharacters,
  );

  return {
    Result: {
      BaseResponse: { bSuccess: true, PlayerId: accountId, FailureReason: "" },
      SelectableCharacters: newCharacters,
      PlayerData: { ...player.PlayerData, CurrencyAmount: newCurrency },
    },
  };
}
