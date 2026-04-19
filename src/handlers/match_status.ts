import { logger, logwrapper, BE_VERBOSE } from "../config/logger";
import express, { Request, Response } from "express";
import { IMatchStatus, IMatchStatusTimestamp } from "../interfaces/IMatchStatus";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { AccountToken, IAccountToken } from "../types/AccountToken";
import * as KitchenSink from "../utils/garbagecan";

const logPrefix: string = "[MatchStatusHandler]";

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
  let errorType: string = "";

  logwrapper.verbose(`${logPrefix} POST /api/ovs_match_status Received match status update for MatchId: ${matchId}`);

  if (event.toLocaleLowerCase().includes("error"))
  {
    errorType = event.toLocaleLowerCase().includes("terminatingerror") ? "TerminatingError" : "NonTerminatingError";
    logger.warn(`${logPrefix} Received ${errorType} event for match ${matchId}. Description: ${description}`);
  }

  if(
    event.toLocaleLowerCase().includes("ServerListening") ||
    event.toLocaleLowerCase().includes("MatchStarted") ||
    event.toLocaleLowerCase().includes("MementoMori") ||
    (event.toLocaleLowerCase().includes("HeartBeat") && description.toLocaleLowerCase().includes("heartbeat loop started"))
  )
  {
    logger.info(`${logPrefix} Received ${event} event for match ${matchId}. Description: ${description}`);
  }

  if (
    event.toLocaleLowerCase().includes("TickPerformance".toLocaleLowerCase()) &&
    description.toLocaleLowerCase().includes(": 1668")
  )
  {
    logger.warn(`${logPrefix} Received TickPerformance event with high tick time for match ${matchId}. Description: ${description}`);
  }

  logwrapper.verbose(`Received match status update: Timestamp=${timestamp.RawTimestamp}, Event=${event}, Description=${description}, MatchId=${matchId}, Key=${key}, NumPlayers=${numPlayers}, PlayerId=${playerId}, PlayerIds=${playerIds.join(",")}`);

  return true;
}
