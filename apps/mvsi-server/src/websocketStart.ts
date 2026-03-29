import { connect } from "@mvsi/database";
import { env } from "@mvsi/env";
import { logger } from "@mvsi/logger";
import { startRedis } from "@mvsi/redis";
import "./modules/friends/friends.ws";
import "./modules/lobby/lobby.ws";
import "./modules/matchmaking/matchmaking.ws";
import "./modules/notifications/notifications.ws";
import { MAIN_WEBSOCKET } from "./websocket.elysia";

Promise.all([connect(), startRedis()]).then(() => {
  const PORT = env.WEBSOCKET_PORT || 3000;
  MAIN_WEBSOCKET.listen(PORT);
  logger.info(`WebSocket server is listening on port ${PORT}`);
});
