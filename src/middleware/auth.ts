import { logger, logwrapper } from "../config/logger";
import { NextFunction, Request, Response } from "express";
import * as jwt from "jsonwebtoken";
import env from "../env/env";
import * as SharedTypes from "../types/shared-types";
import { clientIpFromHeaders } from "../utils/clientIp";

declare global {
  namespace Express {
    interface Request {
      token: SharedTypes.IAccountToken;
      rawToken: string;
      realIp: string | undefined | null;
      requestForwarded: boolean;
    }
  }
}

const serviceName = "Middleware.Auth";
const logPrefix = `[${serviceName}]:`;

export const HYDRA_ACCESS_TOKEN = "x-hydra-access-token";
export { REAL_IP_HEADER, FORWARDED_FOR_HEADER, FORWARDED_FOR_HOST_HEADER } from "../utils/clientIp";
export const SECRET = "SHHHH!!";

export function decodeToken(token: string) {
  return jwt.verify(token, SECRET) as SharedTypes.IAccountToken;
}

export const hydraTokenMiddleware = (req: Request, res: Response, next: NextFunction): void => {
  if (req.url === "/access" || req.url.includes("/sessions/auth/token")) {
    return next();
  }

  // Browser-facing static asset routes — no hydra token, served directly to browser <img>.
  // Without this bypass, prod (where req.hostname !== WB_DOMAIN) hits the 401 path below
  // which sets status but never calls next() or sends a body, hanging the request until
  // the proxy times out → broken image icons. Local dev usually passes the hostname check
  // (localhost === localhost) so the bypass doesn't fire and images "work" by accident.
  if (
    req.url.startsWith("/images/") ||
    req.url.startsWith("/favicon/") ||
    req.url === "/favicon.ico"
  ) {
    return next();
  }

  if (req.hostname === env.WB_DOMAIN) {
    return next();
  }

  // A /batch sub-request is a copy of the batch's request (Object.create) with the game's own per-call headers, which
  // carry no proxy headers: it keeps the address resolved for the batch, which it inherits.
  //@ts-ignore
  if (!req.batch) {
    const resolvedIP = getRealIP(req);
    req.realIp = resolvedIP.ip;
    req.requestForwarded = resolvedIP.isForwarded;
  }
  const token = req.headers[HYDRA_ACCESS_TOKEN];

  if (typeof token === "string") {
    try {
      req.rawToken = token;
      req.token = decodeToken(token);
    }
    catch(e) {
      logger.error(e)
    }
    return next();
  } else {
    // If the token is missing or invalid (null or undefined), send an unauthorized response
    res.status(401);
  }
};

/**
 * The client's address behind the reverse proxy (see clientIpFromHeaders). req.ip, the connection's own, is read only
 * when no header names the client, and never throws: on a /batch sub-request (a copy of the batch's request) Express's
 * ip getter finds no socket and throws.
 */
export function getRealIP(req: Request): { ip: string; isForwarded: boolean } {
  return clientIpFromHeaders(req.headers, () => {
    try {
      return req.ip;
    } catch {
      return req.socket?.remoteAddress;
    }
  });
}

export function tryGetRealIP(req: Request): string {
  try {
    if (req.realIp)
    {
      logwrapper.verbose(`${logPrefix} Raw req.ip is: ${req.ip}; Using cached real IP: ${req.realIp}`);
      return req.realIp;
    }
    else {
      const realIP = getRealIP(req).ip;
      logwrapper.verbose(`${logPrefix} Raw req.ip is: ${req.ip}; Resolved real IP: ${realIP}`);
      return realIP;
    }
  } catch (error) {
    logwrapper.verbose(`${logPrefix} Error resolving real IP: ${error}; Falling back to req.ip: ${req.ip}`);
    return req.ip || "";
  }
}
