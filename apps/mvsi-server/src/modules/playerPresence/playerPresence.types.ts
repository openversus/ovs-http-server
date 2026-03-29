import type { SERVER_MODESTRING } from "../matchmaking/matchmaking.types";

export type PlayerPresence = {
  defaultGamemode: SERVER_MODESTRING;
  currentLobbyId: string;
  ip: string;
};
