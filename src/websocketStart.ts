import { logger } from "./config/logger";
import { startRedis } from "./config/redis";
import { WebSocketService } from "./websocket";
import * as http from "http";
import * as fs from "fs";
import path from "path";
import env from "./env/env";

const serviceName: string = "WebSocketStart";

let shuttingDown = false;
function fatal(err: any, origin: string) {
  
  if (shuttingDown)
  {
    return;
  }
  
  shuttingDown = true;

  logger.error(`[${serviceName}]: [FATAL] ${origin}: ${err?.stack || err}`);
  process.exit(1); // non-zero => Docker treats it as failure and restart policy applies
}

process.on("uncaughtException", (reason, promise) => {
  console.error(`[${serviceName}] Unhandled Exception at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    fatal(reason, "uncaughtException");
  }
  process.exit(1);
});

process.on("unhandledRejection", (reason, promise) => {
  console.error(`[${serviceName}] Unhandled Rejection at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    fatal(reason, "unhandledRejection");
  }
  process.exit(1);
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
