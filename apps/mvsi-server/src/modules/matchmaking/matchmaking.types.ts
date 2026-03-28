import { ArenaConstants, ArenaData } from "../lobby/arena.lobby.types";
import { NotificationTemplate } from "../notifications/notifications.types";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";
import { Region } from "./matchmaking.matching";

export const MATCHMAKING_MATCH_FOUND_CHANNEL = "matchmaking:matchfound";
export const MATCHMAKING_MATCH_TICK_CHANNEL = "matchmaking:tick";
export const MATCHMAKING_COMPLETE_CHANNEL = "matchmaking:complete";
export const MATCHMAKING_CANCEL_CHANNEL = "matchmaking:cancel";
export const MATCHMAKING_PERKS_LOCKED_CHANNEL = "matchmaking:perkslocked";
export const ACTIVEMATCH_END_CHANNEL = "activematch:end";

export enum MATCH_TYPES {
  ONE_V_ONE = "1v1",
  TWO_V_TWO = "2v2",
  FFA = "FFA",
  CASUAL = "casual",
  ONE_V_ONE_RANKED = "ranked-1v1",
  TWO_V_TWO_RANKED = "ranked-2v2",
  RIFTS = "rifts",
  ARENA = "arena",
}

export const MATCH_STATE = {
  QUEUED: 2,
  CANCELLED: 3,
};

export type MatchmakingPlayer = {
  id: string;
  skill: number;
  region: string;
};

export type TicketRegionLatency = {
  region: string;
  latency: number;
};

export type MatchmakingTicket = {
  partySize: number;
  /** Primary (lowest-latency) region for this party */
  region: string;
  /** All regions sorted by latency (lowest first) */
  regions: TicketRegionLatency[];
  skill: number;
  playerIds: string[];
  /** For arena: each inner array is one preserved team; for regular modes: [[...playerIds]] */
  teams: string[][];
  created_at: Date;
  partyId: string;
  matchmakingRequestId: string;
  matchType: MATCH_TYPES;
  partyLeaderId: string;
};

export type MatchmakingCompleteMessage = {
  containerMatchId: string;
  matchmakingRequestId: string;
  resultId: string;
  playerIds: string[];
};

export type MatchmakingCancelMessage = {
  playersIds: string[];
  matchmakingRequestId: string;
};

export type MatchEndMessage = {
  playersIds: string[];
  matchId: string;
};

export interface MatchmakingPlayerConfig {
  playerId: string;
  partyId: string;
  playerIndex: number;
  teamIndex: 0 | 1;
  isHost: boolean;
  ip: string;
}

export type MatchmakingPerksLockMessage = {
  containerMatchIdKey: string;
  playerIds: string[];
};

export type GameplayConfig = {
  ArenaId?: string;
  ArenaConstants?: ArenaConstants;
  ArenaData?: ArenaData;
  GameplayConfig: MatchConfig;
};

export type ActiveMatch = {
  matchKey: string;
  fromLobbyId: string;
  GameplayConfig: GameplayConfig;
  state: string;
  createdAt: number;
};

type CustomGameSettings = {
  bHazardsEnabled: boolean;
  bShieldsEnabled: boolean;
  MatchTime: number;
  NumRingouts: number;
};

type HudSettings = {
  bDisplayPortraits: boolean;
  bDisplayStocks: boolean;
  bDisplayTimer: boolean;
};

type MatchConfig = {
  ArenaModeInfo: {
    FaceoffWaitTime: number;
    bReadyToStart: boolean;
    ShopTime: number;
  } | null;
  RiftNodeId: string;
  ScoreEvaluationRule: string;
  bIsPvP: boolean;
  ScoreAttributionRule: string;
  MatchDurationSeconds: number;
  Created: Date;
  EventQueueSlug: string;
  bModeGrantsProgress: boolean;
  TeamData: any[];
  Spectators: any;
  bIsRanked: boolean;
  bIsCustomGame: boolean;
  Players: { [key: string]: PlayerConfig };
  CustomGameSettings: CustomGameSettings;
  HudSettings: HudSettings;
  bIsCasualSpecial: boolean;
  bAllowMapHazards: boolean;
  RiftNodeAttunement: string;
  CountdownDisplay: string;
  Cluster: Region;
  WorldBuffs: any[];
  bIsTutorial: boolean;
  MatchId: string;
  bIsOnlineMatch: boolean;
  ModeString: string;
  Map: string;
  bIsRift: boolean;
};

type Payload = {
  match: {
    id: string;
  };
  custom_notification: string;
};

type MatchData = {
  MatchId: string;
  GameplayConfig: MatchConfig;
  template_id: string;
  [key: string]: unknown;
};

export type GameNotification = {
  data: MatchData;
  payload: Payload;
  header: string;
  cmd: string;
};

export type MatchFoundChannelMessage = {
  matchId: string;
  matchKey: string;
  regionId: string;
  playerIds: string[];
  gameNotification: NotificationTemplate;
};
