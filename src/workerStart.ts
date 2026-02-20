import { logger } from "./config/logger";
import { startRedis } from "./config/redis";
import { startMatchMakingWorker } from "./matchmaking-worker";

const serviceName: string = "MatchmakingWorker";

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

startRedis().then(() => {
  startMatchMakingWorker();
});
