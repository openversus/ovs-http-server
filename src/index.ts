// import { startRedis } from "./config/redis";

// import { start } from "./server";

// startRedis().then(() => {
//   start();
// });

import { startRedis } from "./config/redis";
import { start } from "./server";

const serviceName: string = "Index";
const logPrefix: string = `[${serviceName}]:`;

process.on("uncaughtException", (reason, promise) => {
  console.error(`[${logPrefix}] Uncaught Exception at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`[${logPrefix}] Stack:`, reason.stack);
  }
//  process.exit(1);
});

process.on("unhandledRejection", (reason, promise) => {
  console.error(`[${logPrefix}] Unhandled Rejection at:`, promise, "reason:", reason);
  // Print full stack trace
  if (reason instanceof Error) {
    console.error(`[${logPrefix}] Stack:`, reason.stack);
  }
//  process.exit(1);
});

startRedis()
  .then(() => {
    start();
  })
  .catch((err) => {
    console.error(`[${logPrefix}] Startup error:`, err);
    if (err instanceof Error) {
      console.error(`[${logPrefix}] Stack trace:`, err.stack);
    }
    process.exit(1);
  });
