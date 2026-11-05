import { logger } from "../config/logger";
import { redisClient } from "../config/redis";
import { DEFAULT_TWOSDAY_WINDOW, isInTwosdayWindow, TwosdayWindow } from "../utils/twosdayWindow";

const logPrefix = "[Twosday]:";

/**
 * Twosday: while it is on and inside its window (Tuesdays 3 PM to midnight US Eastern by default), every 1v1 and 2v2
 * queue goes to the 2v2 queue (queueMatch), and no party lobby can be formed: party invites are not delivered and the
 * party join paths refuse a new member. Everyone solo queues 2v2 and gets a random partner. Custom lobbies, their
 * invites and their joins are untouched. A party formed before the window keeps working and can still queue together.
 *
 * Switched hot through the Redis hash `twosday`, read on every check (no restart). On by default: the HTTP server writes
 * `enabled 1` at startup when the field is missing (ensureTwosdaySwitch), and a missing field reads as on, so an explicit
 * off survives restarts and deleting the key does not turn it off:
 *   HSET twosday enabled 0        off (also false / off); 1 or no field: on
 *   HSET twosday day 2            0 Sunday .. 6 Saturday (default 2, Tuesday)
 *   HSET twosday start 15:00      HH:MM or HH:MM:SS, inclusive (default 15:00)
 *   HSET twosday end 24:00        HH:MM or HH:MM:SS, exclusive, 24:00 = midnight (default 24:00)
 *   HSET twosday tz America/New_York   the clock the hours are on (default America/New_York, daylight saving included)
 *   HSET twosday breakup_minutes 30    for how long after the window opens the party lobbies formed before it are broken
 *                                      up (twosdayPartyBreakup; default 30; 0: never)
 * A setting that cannot be read (a bad time, day or zone) or Redis failing counts as off, with an error logged.
 */
export interface TwosdaySettings {
  enabled: boolean;
  window: TwosdayWindow;
  breakupMinutes: number;
}

/** The switch as stored in Redis, defaults filled in. Throws when Redis fails or a value cannot be read. */
export async function readTwosdaySettings(): Promise<TwosdaySettings> {
  const config = (await redisClient.hGetAll("twosday")) ?? {};
  const breakupMinutes = config.breakup_minutes !== undefined && config.breakup_minutes !== "" ? Number(config.breakup_minutes) : 30;
  if (!Number.isFinite(breakupMinutes) || breakupMinutes < 0) throw new Error(`bad Twosday breakup_minutes: ${config.breakup_minutes}`);
  return {
    enabled: !["0", "false", "off"].includes((config.enabled ?? "").trim().toLowerCase()),
    window: {
      day: config.day !== undefined && config.day !== "" ? Number(config.day) : DEFAULT_TWOSDAY_WINDOW.day,
      start: config.start || DEFAULT_TWOSDAY_WINDOW.start,
      end: config.end || DEFAULT_TWOSDAY_WINDOW.end,
      timeZone: config.tz || DEFAULT_TWOSDAY_WINDOW.timeZone,
    },
    breakupMinutes,
  };
}

export async function isTwosdayActive(now: Date = new Date()): Promise<boolean> {
  try {
    const settings = await readTwosdaySettings();
    return settings.enabled && isInTwosdayWindow(now, settings.window);
  } catch (e) {
    logger.error(`${logPrefix} Could not read the twosday setting, treating it as off: ${e}`);
    return false;
  }
}

/** At startup: switches Twosday on unless the switch is already set (on or off). A failure is logged, never fatal. */
export async function ensureTwosdaySwitch(): Promise<void> {
  try {
    const created = await redisClient.hSetNX("twosday", "enabled", "1");
    const enabled = await redisClient.hGet("twosday", "enabled");
    logger.info(`${logPrefix} Switch ${created ? "created on (default)" : `already set: enabled=${enabled}`}`);
  } catch (e) {
    logger.error(`${logPrefix} Could not set the default twosday switch: ${e}`);
  }
}
