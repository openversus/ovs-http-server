import { logger } from "./config/logger";
import { startRedis } from "./config/redis";
import { startMatchMakingWorker } from "./matchmaking-worker";

const serviceName: string = "MatchmakingWorker";
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

startRedis().then(() => {
  startMatchMakingWorker();
});
