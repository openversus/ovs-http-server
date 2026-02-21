import pino from "pino";
import env from "../env/env";

export const BE_VERBOSE: boolean = env.VERBOSE_LOGGING === 0 ? false : true;

export const logger = pino({
  transport: {
    target: "pino-pretty",
    options: {
      ignore: "pid,hostname",
    },
  },
});
logger.level = "trace";

interface ILogWrapper {
  verbose(message: string, ...args: any[]): void;
  info(message: string, ...args: any[]): void;
  error(message: string, ...args: any[]): void;
  warn(message: string, ...args: any[]): void;
  debug(message: string, ...args: any[]): void;
  trace(message: string, ...args: any[]): void;
}

export class LogWrap implements ILogWrapper {
  static classlogger: pino.Logger;

  constructor() {
    if (!LogWrap.classlogger) {
      LogWrap.classlogger = logger;
    }
  }

  verbose(message: string, ...args: any[]): void {
    if (BE_VERBOSE) {
      LogWrap.classlogger.info(message, ...args);
    }
  }

  info(message: string, ...args: any[]): void {
    LogWrap.classlogger.info(message, ...args);
  }

  error(message: string, ...args: any[]): void {
    LogWrap.classlogger.error(message, ...args);
  }

  warn(message: string, ...args: any[]): void {
    LogWrap.classlogger.warn(message, ...args);
  }

  debug(message: string, ...args: any[]): void {
    LogWrap.classlogger.debug(message, ...args);
  }

  trace(message: string, ...args: any[]): void {
    LogWrap.classlogger.trace(message, ...args);
  }
}

export const logwrapper: ILogWrapper = new LogWrap();
