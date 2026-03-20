import { env } from "@mvsi/env";
import { logger } from "@mvsi/logger";
import { ObjectId } from "mongodb";
import { encodeHydraWS, MAIN_WEBSOCKET } from "../../websocket.elysia";
import {
  ARENA_MATCH_FOUND_CHANNEL,
  getArenaConstants,
  type ArenaMatchFoundMessage,
} from "./arena.lobby.service";

const subscriber = MAIN_WEBSOCKET.decorator.redisSub;
const clients = MAIN_WEBSOCKET.decorator.players;

subscriber.subscribe(ARENA_MATCH_FOUND_CHANNEL, (message) => {
  const notification = JSON.parse(message) as ArenaMatchFoundMessage;
  handleArenaMatchFound(notification);
});

function handleArenaMatchFound(notification: ArenaMatchFoundMessage) {
  const { arenaId, matchId, playerIds, arenaState, matchConfig, matchKey } = notification;

  // Subscribe each real player to the match topic
  for (const playerId of playerIds) {
    const client = clients.get(playerId);
    if (client) {
      client.subscribe(matchId);
    }
  }

  // GameServerReadyNotification
  const serverReadyMessage = {
    data: {
      MatchKey: matchKey,
      MatchID: matchId,
      Port: env.UDP_PORT,
      template_id: "GameServerReadyNotification",
      IPAddress: env.UDP_SERVER_IP,
    },
    payload: {
      match: { id: matchId },
      custom_notification: "realtime",
    },
    header: "",
    cmd: "update",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(serverReadyMessage));

  // OnGameplayConfigNotified with full arena data
  const gameplayConfigMessage = {
    data: {
      ArenaId: arenaId,
      ArenaData: arenaState,
      ArenaConstants: getArenaConstants(),
      MatchId: matchId,
      GameplayConfig: matchConfig,
      template_id: "OnGameplayConfigNotified",
    },
    payload: {
      match: { id: matchId },
      custom_notification: "realtime",
    },
    header: "",
    cmd: "update",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(gameplayConfigMessage));

  // game-server-instance-ready
  const gameServerReadyMessage = {
    data: {},
    payload: {
      game_server_instance: {
        game_server_type_slug: "multiplay",
        port: env.UDP_PORT,
        owner_id: matchId,
        host: env.UDP_SERVER_IP,
        id: new ObjectId().toHexString(),
      },
      proxied_data: null,
    },
    header: "Your game server is ready to join.",
    cmd: "game-server-instance-ready",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(gameServerReadyMessage));

  logger.info(`Arena match started: arenaId=${arenaId}, matchId=${matchId}`);
}
