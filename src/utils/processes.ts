import { logger } from "../config/logger";
import { exec, spawn, spawnSync, SpawnOptions } from "child_process";

const rollbackExecutable = "/app/rollback-server";

export function execRollbackProcess(port: number): void {
  const command = `${rollbackExecutable} ${port}`;
  const options = {
    timeout: 495,
  };
  exec(command, options, (error, stdout, stderr) => {
    if (error) {
      logger.error(`Error executing rollback process on port ${port}:`, error);
      return;
    }
    if (stderr) {
      logger.error(`Rollback process stderr on port ${port}:`, stderr);
      return;
    }
    logger.info(`Rollback process stdout on port ${port}:`, stdout);
  });
}

export async function execRollbackProcessAsync(port: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const command = `${rollbackExecutable} ${port}`;
    const options = {
      timeout: 495,
    };
    exec(command, options, (error, stdout, stderr) => {
      if (error) {
        logger.error(`Error executing rollback process on port ${port}:`, error);
        reject(error);
        return;
      }
      if (stderr) {
        logger.error(`Rollback process stderr on port ${port}:`, stderr);
        reject(new Error(stderr));
        return;
      }
      logger.info(`Rollback process stdout on port ${port}:`, stdout);
      resolve();
    });
  });
}


// export function startRollbackProcess(port: number): void {
//   const args = [`${port}`];
  
//   const options: SpawnOptions = {
//     // detached: true,
//     // stdio: "ignore", // Ignore stdio to run in the background
//     timeout: 430,
//   };
//   const child = spawn(rollbackExecutable, args, options);
//   child.unref();
// }

// export async function startRollbackProcessAsync(port: number): Promise<void> {
//   return new Promise((resolve, reject) => {
//     const args = [` ${port}`];
    
//     const options: SpawnOptions = {
//       detached: true,
//       stdio: "ignore", // Ignore stdio to run in the background
//       timeout: 430,
//     };
//     logger.info(`Starting rollback process on port ${port} with executable ${rollbackExecutable}`);
//     const child = spawn(rollbackExecutable, args, options);
//     child.on("error", (err) => {
//       logger.error(`Error starting rollback process on port ${port}:`, err);
//       reject(err);
//     });
//     child.on("spawn", () => {
//       logger.info(`Rollback process started on port ${port} with PID ${child.pid}`);
//       child.unref();
//       resolve();
//     });
//   });
// }

// export function startRollbackProcessLazy(port: number): void {
//   setTimeout(() => {
//     startRollbackProcessAsync(port);
//   }, 2);
// }
