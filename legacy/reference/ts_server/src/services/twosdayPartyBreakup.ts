import { logger } from "../config/logger";
import { redisClient, RedisLobbyState } from "../config/redis";
import { performGenuineLeave } from "../ssc/ssc";
import { readTwosdaySettings } from "./twosdayService";
import { secondsIntoTwosdayWindow } from "../utils/twosdayWindow";

const logPrefix = "[Twosday]:";
const MESSAGE = "Parties are off for Twosday: queue solo for a random partner";
const INTERVAL_MS = 15_000;

/**
 * Twosday's party breakup: for the first `breakup_minutes` of the window (default 30; see twosdayService), every 15 s,
 * each party lobby (`lobby:{id}`, 2+ players) formed before the window is broken up, as soon as none of its players is
 * queued, in a match or in a ranked set: the owner keeps the lobby, the others get a solo lobby of their own, and both
 * games are told (performGenuineLeave, the leave path; its party_left notification is what the DLL acts on). A party
 * that is busy when the window opens is broken up once it is idle, if that is still within those minutes. Custom
 * lobbies are not party lobbies (no playerIds) and are never touched. One server instance works each round (a 10 s
 * lock), so running several is safe.
 */
export async function breakUpIdleParties(now: Date = new Date()): Promise<number> {
  let settings;
  try {
    settings = await readTwosdaySettings();
  } catch (e) {
    logger.error(`${logPrefix} Party breakup skipped: could not read the twosday setting: ${e}`);
    return 0;
  }
  if (!settings.enabled || settings.breakupMinutes <= 0) return 0;
  let into: number | null;
  try {
    into = secondsIntoTwosdayWindow(now, settings.window);
  } catch (e) {
    logger.error(`${logPrefix} Party breakup skipped: ${e}`);
    return 0;
  }
  if (into === null || into >= settings.breakupMinutes * 60) return 0;

  if (!(await redisClient.set("twosday:breakup_lock", "1", { NX: true, EX: 10 }))) return 0;

  let brokenUp = 0;
  for await (const key of redisClient.scanIterator({ MATCH: "lobby:*", COUNT: 200 })) {
    try {
      const lobbyId = key.slice("lobby:".length);
      const raw = await redisClient.get(key);
      if (!raw) continue;
      const lobby = JSON.parse(raw) as RedisLobbyState;
      if (!Array.isArray(lobby.playerIds) || lobby.playerIds.length < 2 || !lobby.ownerId) continue;
      if (await busy(lobby.playerIds)) continue;
      const leaving = lobby.playerIds.find((pid) => pid !== lobby.ownerId);
      if (!leaving) continue;
      logger.info(`${logPrefix} Breaking up party lobby ${lobbyId} (players ${lobby.playerIds.join(", ")})`);
      await performGenuineLeave(leaving, lobbyId, lobby, MESSAGE);
      brokenUp++;
    } catch (e) {
      logger.error(`${logPrefix} Party breakup: ${key}: ${e}`);
    }
  }

  if (brokenUp > 0) logger.info(`${logPrefix} Broke up ${brokenUp} party lobbies`);
  return brokenUp;
}

// Queued, in a match, or between the games of a ranked set: left alone until the next round.
async function busy(playerIds: string[]): Promise<boolean> {
  for (const pid of playerIds) {
    const status = await redisClient.hGet(`player:${pid}`, "status");
    if (status === "queued" || status === "in_match") return true;
    if (await redisClient.exists(`player_ranked_set:${pid}`)) return true;
  }
  return false;
}

/** Starts the breakup rounds (every 15 s; outside the breakup minutes a round is one read of the switch). */
export function startTwosdayPartyBreakup(): void {
  let running = false;
  const timer = setInterval(async () => {
    if (running) return;
    running = true;
    try {
      await breakUpIdleParties();
    } catch (e) {
      logger.error(`${logPrefix} Party breakup round failed: ${e}`);
    } finally {
      running = false;
    }
  }, INTERVAL_MS);
  timer.unref?.();
}
