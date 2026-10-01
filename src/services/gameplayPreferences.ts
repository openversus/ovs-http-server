import type { NextFunction, Request, Response } from "express";
import { Types } from "mongoose";
import { logger } from "../config/logger";
import { redisClient } from "../config/redis";
import { PlayerTesterModel } from "../database/PlayerTester";
import * as AuthUtils from "../utils/auth";
import { parseGameplayPreferences } from "../utils/gameplayPreferences";

const logPrefix = "[Services.GameplayPreferences]:";

/**
 * Stores the player's GameplayPreferences (see utils/gameplayPreferences.ts): on the player record (Mongo), in their
 * session (connections:{id}, only when it exists) and in the legacy IP-keyed copy while it is theirs. Nothing is written for
 * a value that is not one (never the default over a real value). Writes only what changed.
 */
export async function saveGameplayPreferences(accountId: string, raw: unknown, ip?: string): Promise<number | null> {
  const value = parseGameplayPreferences(raw);
  if (value === null || !accountId || !Types.ObjectId.isValid(accountId)) return null;

  await PlayerTesterModel.updateOne(
    { _id: new Types.ObjectId(accountId), GameplayPreferences: { $ne: value } },
    { $set: { GameplayPreferences: value } },
  );
  const key = `connections:${accountId}`;
  const stored = await redisClient.hGet(key, "GameplayPreferences");
  if (stored !== String(value) && (await redisClient.exists(key))) {
    await redisClient.hSet(key, "GameplayPreferences", String(value));
  }
  // The legacy IP-keyed copy of the session, only while it is this player's (a household shares an IP).
  if (ip && (await redisClient.hGet(`connections:${ip}`, "id")) === accountId) {
    await redisClient.hSet(`connections:${ip}`, "GameplayPreferences", String(value));
  }
  if (stored !== String(value)) {
    logger.info(`${logPrefix} GameplayPreferences for ${accountId}: ${stored ?? "none"} -> ${value}`);
  }
  return value;
}

/**
 * Before the party-lobby requests: records the GameplayPreferences the game sends with them, so the handler and every
 * match after it use the player's current value. Never blocks the request.
 */
export async function recordGameplayPreferencesFromRequest(req: Request, _res: Response, next: NextFunction) {
  try {
    const raw = (req.body as { GameplayPreferences?: unknown } | undefined)?.GameplayPreferences;
    if (raw !== undefined) {
      const account = AuthUtils.DecodeClientToken(req);
      if (account?.id) await saveGameplayPreferences(account.id, raw, account.current_ip);
    }
  }
  catch (error) {
    logger.error(`${logPrefix} Could not record GameplayPreferences from ${req.originalUrl || req.url}: ${error}`);
  }
  next();
}
