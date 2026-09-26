import type { NextFunction, Request, Response } from "express";
import { redisClient, redisRequestClientUpdateModal } from "../config/redis";
import { logger } from "../config/logger";
import env from "../env/env";
import { resolveAccountFromRequest } from "./identityService";
import { isClientGameplayAccessRequiredForMinimum } from "./clientVersion";

const logPrefix = "[Services.ClientUpdateGate]:";

export const CLIENT_UPDATE_URL = "https://prod.openversus.org/update";
export const CLIENT_UPDATE_MESSAGE =
  "A required OpenVersus update is available. Download and install it before playing online.";

export interface ClientUpdateState {
  accountId: string;
  clientVersion: string;
  identityRegistered: boolean;
  required: boolean;
}

function hasRegisteredIdentity(value: unknown): boolean {
  return value === "1" || value === true;
}

export async function getPlayerClientUpdateState(playerId: string): Promise<ClientUpdateState> {
  const connection = playerId
    ? await redisClient.hGetAll(`connections:${playerId}`)
    : {};
  const clientVersion = connection?.clientVersion || "";
  const identityRegistered = hasRegisteredIdentity(connection?.identityRegistered);
  return {
    accountId: playerId,
    clientVersion,
    identityRegistered,
    required: isClientGameplayAccessRequiredForMinimum(
      clientVersion,
      env.MIN_CLIENT_VERSION,
      identityRegistered,
    ),
  };
}

export async function getRequestClientUpdateState(req: Request): Promise<ClientUpdateState> {
  const token = (req as any).token as { id?: string; clientVersion?: string; identityRegistered?: string } | undefined;
  const connection = await resolveAccountFromRequest(req);
  const accountId = connection?.id || token?.id || "";
  const clientVersion = connection?.clientVersion || token?.clientVersion || "";
  const identityRegistered = hasRegisteredIdentity(
    connection?.identityRegistered || token?.identityRegistered,
  );
  return {
    accountId,
    clientVersion,
    identityRegistered,
    required: isClientGameplayAccessRequiredForMinimum(
      clientVersion,
      env.MIN_CLIENT_VERSION,
      identityRegistered,
    ),
  };
}

export async function getPlayersRequiringClientUpdate(playerIds: string[]): Promise<ClientUpdateState[]> {
  const uniqueIds = Array.from(new Set(playerIds.filter(Boolean)));
  const states = await Promise.all(uniqueIds.map(getPlayerClientUpdateState));
  return states.filter((state) => state.required);
}

export async function requestClientUpdateModalsForPlayers(playerIds: string[]): Promise<void> {
  const uniqueIds = Array.from(new Set(playerIds.filter(Boolean)));
  await Promise.all(uniqueIds.map((playerId) => redisRequestClientUpdateModal(playerId)));
}

export function hydraClientUpdateFailure() {
  return {
    body: {
      error: "client_update_required",
      ErrorCode: "ClientOutdated",
      ErrorMessage: CLIENT_UPDATE_MESSAGE,
      MinimumVersion: env.MIN_CLIENT_VERSION,
      UpdateUrl: CLIENT_UPDATE_URL,
    },
    metadata: null,
    return_code: 1,
  };
}

export function jsonClientUpdateFailure() {
  return {
    error: "client_update_required",
    message: CLIENT_UPDATE_MESSAGE,
    minimumVersion: env.MIN_CLIENT_VERSION,
    updateUrl: CLIENT_UPDATE_URL,
  };
}

/**
 * Gate a gameplay transition while keeping login and non-gameplay navigation
 * available. Hydra actions use HTTP 200 + return_code=1 so the old game does
 * not treat the response as a transport failure and retry indefinitely.
 */
export async function requireCurrentClientForGameplay(
  req: Request,
  res: Response,
  next: NextFunction,
) {
  try {
    const state = await getRequestClientUpdateState(req);
    if (!state.required) {
      next();
      return;
    }

    const modalRequested = await redisRequestClientUpdateModal(state.accountId);
    if (modalRequested) {
      logger.warn(
        `${logPrefix} Blocked ${req.method} ${req.originalUrl || req.url} for ${state.accountId || "unresolved"} `
        + `(version=${state.clientVersion || "legacy"}, identity=${state.identityRegistered ? "registered" : "missing"}, `
        + `minimum=${env.MIN_CLIENT_VERSION || "disabled"}); `
        + "requested reward-free update toast",
      );
    }
    res.status(200).send(hydraClientUpdateFailure());
  } catch (error) {
    logger.error(`${logPrefix} Failed to evaluate gameplay gate: ${error}`);
    res.status(503).send(hydraClientUpdateFailure());
  }
}
