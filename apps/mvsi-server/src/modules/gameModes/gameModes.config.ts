import { maps1v1, maps2v2 } from "../../data/maps";
import type { SERVER_MODESTRING } from "../matchmaking/matchmaking.types";
import type { PlayerConfig } from "../playerConfig/playerConfig.types";

export type PlayersConfigObject = {
  [playerId: string]: PlayerConfig;
};

type GameModeTeamConfig = {
  Players: unknown[];
  Required: boolean;
  MinPlayerPerTeam: number;
  MaxPlayerPerTeam: number;

  RequiredTeamPlayerBuffs: string[];
};

export enum TeamStyle {
  Solos = "Solos",
  Duos = "Duos",
  FFA = "FFA",
  Other = "Other",
}

export type GameModeConfig = {
  TeamStyle: TeamStyle;
  MapRotation: string[];
  GameModeTeams: GameModeTeamConfig[];
  MatchDuration: number;
  bShieldsEnabled: boolean;
  bHazardsEnabled: boolean;
  NumRingouts: number;
};

const ONE_V_ONE_GAMEPLAY_CONFIG: GameModeConfig = {
  TeamStyle: TeamStyle.Solos,
  MapRotation: maps1v1,
  MatchDuration: 420,
  NumRingouts: 3,
  bHazardsEnabled: false,
  bShieldsEnabled: true,
  GameModeTeams: [
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
  ],
};

const TWO_V_TWO_GAMEPLAY_CONFIG: GameModeConfig = {
  TeamStyle: TeamStyle.Duos,
  MapRotation: maps2v2,
  MatchDuration: 420,
  bHazardsEnabled: false,
  bShieldsEnabled: true,
  NumRingouts: 4,
  GameModeTeams: [
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 2,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 2,
      RequiredTeamPlayerBuffs: [],
    },
  ],
};

const FFA_GAMEPLAY_CONFIG: GameModeConfig = {
  TeamStyle: TeamStyle.FFA,
  MapRotation: maps2v2,
  MatchDuration: 420,
  bHazardsEnabled: false,
  bShieldsEnabled: true,
  NumRingouts: 4,
  GameModeTeams: [
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
  ],
};

const CASUAL_GAMEPLAY_CONFIG: GameModeConfig = {
  TeamStyle: TeamStyle.FFA,
  MapRotation: maps2v2,
  MatchDuration: 60,
  bHazardsEnabled: false,
  bShieldsEnabled: true,
  NumRingouts: 1,
  GameModeTeams: [
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
    {
      Players: [],
      Required: true,
      MinPlayerPerTeam: 1,
      MaxPlayerPerTeam: 1,
      RequiredTeamPlayerBuffs: [],
    },
  ],
};

export const GAME_MODES = new Map<SERVER_MODESTRING, GameModeConfig>();

GAME_MODES.set("1v1", ONE_V_ONE_GAMEPLAY_CONFIG);
GAME_MODES.set("2v2", TWO_V_TWO_GAMEPLAY_CONFIG);
GAME_MODES.set("FFA", FFA_GAMEPLAY_CONFIG);
GAME_MODES.set("casual", CASUAL_GAMEPLAY_CONFIG);
