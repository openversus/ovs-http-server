import { logger, logwrapper, BE_VERBOSE } from "../config/logger";
import express, { Request, Response } from "express";
import { IMatchStatus, IMatchStatusTimestamp } from "../interfaces/IMatchStatus";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { AccountToken, IAccountToken } from "../types/AccountToken";
import * as KitchenSink from "../utils/garbagecan";

export async function handleMatchStatusUpdate(req: Request<{}, {}, IMatchStatus, {}>, res: Response): Promise<boolean> {
  const statusObject = (req.body as IMatchStatus) || ({} as IMatchStatus);
  const timestamp = (statusObject.Timestamp as IMatchStatusTimestamp) || ({} as IMatchStatusTimestamp);
  const event = statusObject.Event ?? "";
  const description = statusObject.Description ?? "";
  const matchId = statusObject.matchId ?? "";
  const key = statusObject.key ?? "";
  const numPlayers = statusObject.NumPlayers ?? 0;
  const playerId = statusObject.PlayerId ?? "";
  const playerIds = statusObject.PlayerIds ?? [];

  logger.info(
    `Received match status update: Timestamp=${timestamp.RawTimestamp}, Event=${event}, Description=${description}, MatchId=${matchId}, Key=${key}, NumPlayers=${numPlayers}, PlayerId=${playerId}, PlayerIds=${playerIds.join(",")}`,
  );

  return true;
}
