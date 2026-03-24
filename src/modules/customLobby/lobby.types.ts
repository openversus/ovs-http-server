import { GAME_MODES_CONFIG } from "./gameModes.data";

// ─── Enums ────────────────────────────────────────────────────────────────────

export enum TeamStyle {
  Solos = "Solos",
  Duos = "Duos",
  FFA = "FFA",
  Other = "Other",
}

export enum MATCH_TYPES {
  ONE_V_ONE = "1v1",
  TWO_V_TWO = "2v2",
  FFA = "FFA",
  CASUAL = "casual",
  ONE_V_ONE_RANKED = "ranked-1v1",
  TWO_V_TWO_RANKED = "ranked-2v2",
  RIFTS = "rifts",
}

// ─── Redis Channels ───────────────────────────────────────────────────────────

export const LOBBY_JOINED_CHANNEL = "lobby:joined";
export const CUSTOM_LOBBY_NOTIFICATION_CHANNEL = "custom_lobby:notification";

// ─── Lobby Player / Team ──────────────────────────────────────────────────────

export type LobbyPlayer = {
  Account: { id: string };
  JoinedAt: Date | string;
  BotSettingSlug: string;
  LobbyPlayerIndex: number;
  CrossplayPreference: number;
  Fighter?: { AssetPath: string; Slug: string };
  Skin?: { AssetPath: string; Slug: string };
};

export type LobbyTeam = {
  TeamIndex: number;
  Players: Record<string, LobbyPlayer>;
  Length: number;
};

export type LockedLoadout = {
  Character: string;
  Skin: string;
};

export type MultiplayParams = {
  MultiplayClusterSlug: string;
  MultiplayProfileId: string;
  MultiplayRegionId: string;
};

// ─── Lobby Structures ─────────────────────────────────────────────────────────

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
};

export type PartyLobby = BaseLobby & {
  ModeString: MATCH_TYPES;
};

export type CustomLobby = BaseLobby &
  CustomLobbySettings & {
    ModeString?: string;
  };

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
  QueueType: string;
  Context: string;
  ModeDifficulty: string;
  GameModeAlias: string;
  NumRingoutsForWin: number;
  MatchDuration: number;
  AllowHazards: boolean;
  AllowDuplicateCharacters: boolean;
  AreRewardsSkipped: boolean;
  num_set_wins_required: number;
  EnableShields: number;
};

// ─── Player Config (for match start) ─────────────────────────────────────────

export type PlayerConfig = {
  AccountId: string;
  Username: string | {};
  bUseCharacterDisplayName: boolean;
  PlayerIndex: number;
  TeamIndex: number;
  Character: string;
  Skin: string;
  Taunts: string[];
  Perks: string[];
  Banner: string;
  ProfileIcon: string;
  RingoutVfx: string;
  bIsBot: boolean;
  BotBehaviorOverride: string;
  BotDifficultyMin: number | null;
  BotDifficultyMax: number | null;
  Buffs: string[];
  StatTrackers: [string, number][];
  Gems: { Gem: string; ChargeLevel: number }[];
  StartingDamage: number;
  Handicap: number;
  GameplayPreferences: number;
  bAutoPartyPreference: boolean;
  PartyMember: string | null;
  PartyId: string | null;
  RankedTier: string | null;
  RankedDivision: number | null;
  WinStreak: number | null;
  IsHost: boolean;
  Ip: string;
};

// ─── Notification Types ───────────────────────────────────────────────────────

export type NotificationData = {
  template_id: string;
} & Record<string, any>;

export type NotificationTemplate = {
  data: NotificationData;
  payload: Record<string, any>;
  header: "";
  cmd: string;
};

export type CustomLobbyNotificationMessage = {
  targetPlayerIds: string[];
  excludePlayerIds: string[];
  notification: NotificationTemplate;
};
