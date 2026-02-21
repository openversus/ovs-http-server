import { logger } from "./config/logger";
import { startRedis } from "./config/redis";
import { startMatchMakingWorker } from "./matchmaking-worker";

const serviceName: string = "MatchmakingWorker";

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

startRedis().then(() => {
  startMatchMakingWorker();
});
