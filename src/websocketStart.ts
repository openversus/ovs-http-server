import { logger } from "./config/logger";
import { startRedis } from "./config/redis";
import { WebSocketService } from "./websocket";
import * as http from "http";
import * as fs from "fs";
import path from "path";
import env from "./env/env";

const serviceName: string = "WebSocketStart";

process.on("uncaughtException", (reason, promise) => {
  console.error(`[${serviceName}] Uncaught Exception at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`[${serviceName}] Stack:`, reason.stack);
  }
//  process.exit(1);
});

process.on("unhandledRejection", (reason, promise) => {
  console.error(`[${serviceName}] Unhandled Rejection at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`[${serviceName}] Stack:`, reason.stack);
  }
//  process.exit(1);
});

const server = http.createServer((req, res) => {
  res.writeHead(200, { "Content-Type": "text/plain" });
  res.end("HTTP server is running\n");
});
const websocket = new WebSocketService(server);
startRedis().then(() => {
  // Start the HTTP server on port 3000
  const PORT = env.WEBSOCKET_PORT || 3000;
  server.listen(PORT, () => {
    logger.info(`[${serviceName}]: WebSocket server is listening on port ${PORT}`);
  });
});
