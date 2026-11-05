import { logger } from "./config/logger";
import { redisClearOnlinePlayers, redisClient, startRedis } from "./config/redis";
import { connect as connectMongo } from "./database/client";
import { WebSocketService } from "./websocket";
import * as http from "http";
import * as fs from "fs";
import path from "path";
import env from "./env/env";

const serviceName: string = "WebSocketStart";
const logPrefix: string = `[${serviceName}]:`;

process.on("uncaughtException", (reason, promise) => {
  console.error(`${logPrefix} Uncaught Exception at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`${logPrefix} Stack:`, reason.stack);
  }
//  process.exit(1);
});

process.on("unhandledRejection", (reason, promise) => {
  console.error(`${logPrefix} Unhandled Rejection at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`${logPrefix} Stack:`, reason.stack);
  }
//  process.exit(1);
});

const server = http.createServer((req, res) => {
  res.writeHead(200, { "Content-Type": "text/plain" });
  res.end("HTTP server is running\n");
});
const websocket = new WebSocketService(server);

// Take this process's players out of the online set before exiting, since
// handleDisconnect never runs for connections that die with the process.
let shuttingDown = false;
async function shutdown(signal: NodeJS.Signals) {
  if (shuttingDown) return;
  shuttingDown = true;
  const playerIds = [...websocket.clients.keys()];
  logger.info(`${logPrefix} ${signal} received, removing ${playerIds.length} players from online set`);
  try {
    if (playerIds.length > 0) {
      await redisClient.sRem("online_players", playerIds);
    }
  } catch (e) {
    logger.error(`${logPrefix} Failed to remove players from online set on ${signal}: ${e}`);
  }
  process.exit(0);
}
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

startRedis().then(async () => {
  await connectMongo();
  // Nobody can be connected to this process yet, so anything in the online set
  // is left over from a previous run (crash, SIGKILL, or a signal that never
  // reached node). Clear it before accepting connections.
  await redisClearOnlinePlayers();
  // Start the HTTP server on port 3000
  const PORT = env.WEBSOCKET_PORT || 3000;
  server.listen(PORT, () => {
    logger.info(`${logPrefix} WebSocket server is listening on port ${PORT}`);
  });
});
