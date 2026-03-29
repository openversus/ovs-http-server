import type { GAME_MODES_CONFIG } from "../../data/gameModes";
import type { TeamStyle } from "../gameModes/gameModes.config";
import type { SERVER_MODESTRING } from "../matchmaking/matchmaking.types";

export const LOBBY_JOINED_CHANNEL = "lobby:joined";
export const LOBBY_QUEUED_CHANNEL = "lobby:queued";

export const lobbyTypesMap = {
  party_lobby: "Party",
  custom_game_lobby: "Custom",
  arena_lobby: "Arena",
  rift_lobby: "Rift",
};

export type LobbyPlayerStatus = {
  id: string;
  ready: boolean;
};

export type LobbyCreatedMessage = {
  lobbyId: string;
  accountId: string;
};

export type LobbyPlayer = {
  Account: { id: string };
  JoinedAt: Date;
  BotSettingSlug: string;
  LobbyPlayerIndex: number;
  CrossplayPreference: number;
};

export type LobbyTeam = {
  TeamIndex: number;
  Players: Record<string, LobbyPlayer>;
  Length: number;
};

export type MultiplayParams = {
  MultiplayClusterSlug: string;
  MultiplayProfileId: string;
  MultiplayRegionId: string;
};

export type RegionLatencyData = {
  latency: number;
  region_id: string;
};

export type PlayerConnectionInfo = {
  game_server_region_data: RegionLatencyData[];
};

export type LockedLoadout = {
  Character: string;
  Skin: string;
};

// Base lobby type without ModeString
export type BaseLobby = {
  Teams: LobbyTeam[];
  LeaderID: string;
  LobbyType: number;
  ReadyPlayers: Record<string, unknown>;
  PlayerGameplayPreferences: Record<string, number>;
  PlayerAutoPartyPreferences: Record<string, boolean>;
  GameVersion: string;
  HissCrc: number;
  Platforms: Record<string, string>;
  AllMultiplayParams: Record<string, MultiplayParams>;
  LockedLoadouts: Record<string, LockedLoadout>;
  IsLobbyJoinable: boolean;
  MatchID: string;
  Template: keyof typeof lobbyTypesMap;
  players_connection_info: Record<string, PlayerConnectionInfo>;
  RematchCount: number;
};

// Regular Lobby requires ModeString
export type PartyLobby = BaseLobby & {
  ModeString: SERVER_MODESTRING;
};

export type RiftLobby = BaseLobby;

export type CustomLobby = BaseLobby & CustomLobbySettings;

export type CustomLobbySettings = {
  GameModeSlug: keyof typeof GAME_MODES_CONFIG;
  Handicaps: Record<string, number>;
  WorldBuffs: string[];
  PlayerBuffs: Record<string, string[]>;
  Maps: { Map: string; IsSelected: boolean }[];
  match_config: CustomLobbyMatchConfig;
  LobbyCode?: string;
};

export type CustomLobbyMatchConfig = {
  TeamStyle: TeamStyle;
  QueueType: "Unselected" | "Ranked" | "Casual" | string;
  Context: "Custom" | "Standard" | string;
  ModeDifficulty: "Unselected" | "Easy" | "Normal" | "Hard" | string;
  GameModeAlias: "Versus" | "Coop" | string;
  NumRingoutsForWin: number;
  MatchDuration: number;
  AllowHazards: boolean;
  AllowDuplicateCharacters: boolean;
  AreRewardsSkipped: boolean;
  num_set_wins_required: number;
  EnableShields: number;
};

export type ArenaLobby = BaseLobby;
