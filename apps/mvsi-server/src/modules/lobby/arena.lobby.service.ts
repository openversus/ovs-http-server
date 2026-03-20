import { logger } from "@mvsi/logger";
import { redisClient } from "@mvsi/redis";
import { ObjectId } from "mongodb";
import { randomBytes, randomUUID } from "node:crypto";
import { getRandomMap2v2 } from "../../data/maps";
import { getAssetsByType } from "../../loadAssets";
import {
  type GameplayConfig,
  MATCHMAKING_MATCH_FOUND_CHANNEL,
  type MatchFoundChannelMessage,
} from "../matchmaking/matchmaking.types";
import { broadcastNotificationToUsers } from "../notifications/notifications.utils";
import { getPlayerConfig } from "../playerConfig/playerConfig.service";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";
import type { ArenaLobby, LobbyCreatedMessage } from "./lobby.types";
import { LOBBY_JOINED_CHANNEL } from "./lobby.types";
import { createBaseLobby, getLobby, notifyLobbyJoined } from "./lobby.service";
import { notifyActiveMatchCreated } from "../matchmaking/matchmaking.service";
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

const ARENA_EX = 2 * 24 * 60 * 60; // 2 days
const MAX_TEAMS = 8;
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

// ─── Bot cosmetics pools ──────────────────────────────────────────────────────

const BOT_NAMES = [
  "AuntieKim",
  "SkyIsBlue",
  "SweetieDown",
  "RoboNickPhD",
  "BristleKemp",
  "Laney03",
  "Florrekko",
  "HarmonicMelody",
  "FelixDcat",
  "DMah",
  "Dark",
  "CelestialBard",
  "NightOwl777",
  "PixelPulse",
  "StarChaser",
  "IronGlitch",
  "ChaosTheory",
  "ZeroGravity",
  "LunarTide",
  "QuantumByte",
];

const BOT_RINGOUT_VFX = [
  "ring_out_vfx_default",
  "ring_out_vfx_aku_fire",
  "ring_out_vfx_adam_west",
  "ring_out_vfx_monster_reveal",
  "ring_out_vfx_firework_show",
  "ring_out_vfx_omega_beam",
  "ring_out_vfx_bat_signal",
  "ring_out_vfx_lunar_rabbit",
  "ring_out_vfx_lasso_of_truth",
  "ring_out_vfx_boom_tube",
  "ring_out_vfx_soothing_energy",
  "ring_out_vfx_house_lannister",
  "ring_out_vfx_house_Targeryen",
  "ring_out_vfx_happy_birday_tweetie",
  "ring_out_vfx_monster_tweety",
  "ring_out_vfx_sugar_spice_everything_nice",
];

const BOT_BANNERS = [
  "banner_default",
  "banner_cheesy_temptations",
  "banner_hole_sweet_hole",
  "banner_lannister_banner",
  "banner_slushy_plushy",
  "banner_scoobtober_bats",
  "banner_presently_shocked_epic",
  "banner_marcyshouse",
  "banner_jokerhaha",
  "banner_foretold_champion_rare",
  "banner_test",
  "banner_3rror_mvs",
  "banner_marvin_wardrobe_v2",
  "banner_housetargaryen_01",
  "banner_tools_of_the_trade",
  "banner_chestnuts_not_included",
];

const BOT_PROFILE_ICONS = [
  "profile_icon_default",
  "profile_icon_dc_bat_batman_1",
  "profile_icon_wb_watertower",
  "profile_icon_cn_at_finn_adventure_time",
  "profile_icon_wb_sd_scooby_snack",
  "profile_icon_wb_sd_mysterymachine",
  "profile_icon_mvs_beach_day",
  "profile_icon_bugsbunny_thewabbit",
  "profileicon_stripe_mogwaistripe",
  "profileicon_wb_sj_oldschooljam",
  "profile_icon_c036_data_corrupted",
];

function randomItem<T>(arr: T[]): T {
  return arr[Math.floor(Math.random() * arr.length)];
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

  // Notify existing lobby members about the new player
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

export async function assembleArenaMatch(arenaLobbyId: string, leaderId: string) {
  const lobby = (await getLobby(arenaLobbyId)) as ArenaLobby | null;
  if (!lobby || lobby.LeaderID !== leaderId) return null;

  const arenaConstants = getArenaConstants();

  // Assign a UUID team ID to each of the 8 lobby teams
  const teamUUIDs: string[] = Array.from({ length: MAX_TEAMS }, () => randomUUID());

  // Fill any team that has fewer than PLAYERS_PER_TEAM with bots
  let globalPlayerIndex = lobby.Teams.reduce((sum, t) => sum + t.Length, 0);
  for (const team of lobby.Teams) {
    const slotsNeeded = PLAYERS_PER_TEAM - team.Length;
    for (let j = 0; j < slotsNeeded; j++) {
      const botId = `Bot${randomUUID()}`;
      team.Players[botId] = {
        Account: { id: botId },
        JoinedAt: new Date(),
        BotSettingSlug: "Medium",
        LobbyPlayerIndex: globalPlayerIndex++,
        CrossplayPreference: 0,
      };
      team.Length++;
    }
  }

  // Identify real players (BotSettingSlug is empty for real players)
  const realPlayerIds: string[] = [];
  for (const team of lobby.Teams) {
    for (const [pid, p] of Object.entries(team.Players)) {
      if (p.BotSettingSlug === "") realPlayerIds.push(pid);
    }
  }

  // Fetch configs for real players
  const playerConfigMap = new Map<string, PlayerConfig>();
  for (const pid of realPlayerIds) {
    const config = await getPlayerConfig(pid);
    if (config) playerConfigMap.set(pid, config);
  }

  // Build PlayerInfo for everyone
  const playerInfo: Record<string, ArenaPlayerInfo> = {};
  for (let i = 0; i < MAX_TEAMS; i++) {
    const team = lobby.Teams[i];
    const teamId = teamUUIDs[i];
    for (const [pid, lobbyPlayer] of Object.entries(team.Players)) {
      const isBot = lobbyPlayer.BotSettingSlug !== "";
      const config = playerConfigMap.get(pid);

      playerInfo[pid] = {
        Loadout: { Character: "", Skin: "" },
        SelectableCharacters: isBot ? [] : getRandomCharacters(SELECTABLE_CHARACTER_COUNT),
        AccountId: pid,
        GameplayPreferences: isBot ? 0 : (lobby.PlayerGameplayPreferences[pid] ?? 964),
        TeamId: teamId,
        PlayerData: createArenaPlayerData(),
        CharacterClass: 0,
        AccountInfo: {
          RingoutVfx:
            config?.RingoutVfx ?? (isBot ? randomItem(BOT_RINGOUT_VFX) : "ring_out_vfx_default"),
          Taunts: config?.Taunts ?? [],
          Banner: config?.Banner ?? (isBot ? randomItem(BOT_BANNERS) : "banner_default"),
          ProfileIcon: config?.ProfileIcon ?? (isBot ? randomItem(BOT_PROFILE_ICONS) : ""),
          Name: (config?.Username as string) ?? (isBot ? randomItem(BOT_NAMES) : ""),
        },
        bIsBot: isBot,
        ...(isBot
          ? {}
          : { Stats: createArenaPlayerStats(), CurrentShop: [], CurrentShopLocal: [] }),
      };
    }
  }

  // Build TeamInfo — pair teams sequentially: 0v1, 2v3, 4v5, 6v7
  const teamInfo: Record<string, ArenaTeamInfo> = {};
  for (let i = 0; i < MAX_TEAMS; i++) {
    const teamId = teamUUIDs[i];
    const opponentIdx = i % 2 === 0 ? i + 1 : i - 1;
    const opponentId = teamUUIDs[opponentIdx];

    const hasPlayedTeam: Record<string, boolean> = {};
    for (let j = 0; j < MAX_TEAMS; j++) {
      if (j !== i) hasPlayedTeam[teamUUIDs[j]] = j === opponentIdx;
    }

    teamInfo[teamId] = {
      TeamId: teamId,
      TeamIndex: i,
      Players: Object.keys(lobby.Teams[i].Players),
      Stats: createArenaTeamStats(),
      LifeRemaining: 100,
      FinalRank: 0,
      RoundOpponents: [opponentId],
      HasPlayedTeam: hasPlayedTeam,
      Matches: [],
    };
  }

  const arenaState: ArenaData = {
    AllMultiplayParams: lobby.AllMultiplayParams,
    PlayerInfo: playerInfo,
    CurrentRound: 1,
    TeamInfo: teamInfo,
    Players: realPlayerIds,
  };

  // Create round-1 matches and notify players (pairs: 0v1, 2v3, 4v5, 6v7)
  for (let i = 0; i < MAX_TEAMS; i += 2) {
    const teamAId = teamUUIDs[i];
    const teamBId = teamUUIDs[i + 1];
    const teamAPlayers = Object.keys(lobby.Teams[i].Players);
    const teamBPlayers = Object.keys(lobby.Teams[i + 1].Players);
    const allMatchPlayers = [...teamAPlayers, ...teamBPlayers];

    const realInMatch = allMatchPlayers.filter((pid) => playerConfigMap.has(pid));
    const isRealMatch = realInMatch.length > 0;
    const matchId = isRealMatch ? new ObjectId().toHexString() : `Sim${randomUUID()}`;

    // Record match in team info
    arenaState.TeamInfo[teamAId].Matches.push(matchId);
    arenaState.TeamInfo[teamBId].Matches.push(matchId);

    if (isRealMatch) {
      const map = getRandomMap2v2();
      arenaState.TeamInfo[teamAId].PriorMap = map;
      arenaState.TeamInfo[teamBId].PriorMap = map;
    }

    // Build match players (TeamIndex 0 = teamA, 1 = teamB)
    const matchPlayers: Record<string, PlayerConfig> = {};
    for (let j = 0; j < allMatchPlayers.length; j++) {
      const pid = allMatchPlayers[j];
      const teamIdx = j < PLAYERS_PER_TEAM ? 0 : 1;
      const isBot = !playerConfigMap.has(pid);
      const info = playerInfo[pid];

      if (isBot) {
        matchPlayers[pid] = {
          AccountId: pid,
          Username: info.AccountInfo.Name,
          bUseCharacterDisplayName: true,
          PlayerIndex: j,
          TeamIndex: teamIdx,
          Character: "",
          Skin: "",
          Taunts: [],
          Perks: [
            "perk_gen_boxer",
            "perk_team_speed_force_assist",
            "perk_purest_of_motivations",
            "perk_gen_well_rounded",
          ],
          Banner: info.AccountInfo.Banner,
          ProfileIcon: info.AccountInfo.ProfileIcon,
          RingoutVfx: info.AccountInfo.RingoutVfx,
          bIsBot: true,
          BotBehaviorOverride: "",
          BotDifficultyMin: 2,
          BotDifficultyMax: 2,
          Buffs: [],
          StatTrackers: [],
          Gems: [],
          StartingDamage: 0,
          Handicap: 0,
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
        const config = playerConfigMap.get(pid)!;
        matchPlayers[pid] = {
          ...config,
          PlayerIndex: j,
          TeamIndex: teamIdx,
          Character: "",
          Skin: "",
          Buffs: [],
          Handicap: 0,
          GameplayPreferences: lobby.PlayerGameplayPreferences[pid] ?? config.GameplayPreferences,
          PartyId: null,
          PartyMember: null,
          IsHost: pid === lobby.LeaderID,
        };
      }
    }

    const map = arenaState.TeamInfo[teamAId].PriorMap ?? getRandomMap2v2();

    const gameplayConfig: GameplayConfig = {
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

    if (isRealMatch) {
      const msg: MatchFoundChannelMessage = {
        matchId,
        matchKey: randomBytes(32).toString("base64"),
        playerIds: realInMatch,
        gameNotification: {
          data: {
            ArenaId: arenaLobbyId,
            ArenaData: arenaState,
            ArenaConstants: getArenaConstants(),
            MatchId: matchId,
            GameplayConfig: gameplayConfig,
            template_id: "OnGameplayConfigNotified",
          },
          payload: { match: { id: matchId }, custom_notification: "realtime" },
          header: "",
          cmd: "update",
        },
      };
      await redisClient.publish(MATCHMAKING_MATCH_FOUND_CHANNEL, JSON.stringify(msg));
    }
  }

  // Persist arena state
  const stateKey = arenaStateKey(arenaLobbyId);
  await redisClient
    .multi()
    .json.set(stateKey, "$", arenaState as Parameters<typeof redisClient.json.set>[2])
    .expire(stateKey, ARENA_EX)
    .exec();

  logger.info(`Arena match started for lobby ${arenaLobbyId}`);
  return arenaState;
}

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
  // Shop phase records; full item processing can be expanded here
  return {};
}

export async function arenaCheckin(
  _arenaParentId: string,
  _arenaRound: number,
  _containerMatchId: string,
  _accountId: string,
) {
  // Between-round checkin; trigger next-round logic here when all players check in
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
