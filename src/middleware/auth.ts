import { logger } from "../config/logger";
import { NextFunction, Request, Response } from "express";
import * as jwt from "jsonwebtoken";
import env from "../env/env";
import * as SharedTypes from "../types/shared-types";
import { isIPAddress } from "../utils/garbagecan";
import { get } from "http";

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

export const HYDRA_ACCESS_TOKEN = "x-hydra-access-token";
export const REAL_IP_HEADER = "x-real-ip";
export const FORWARDED_FOR_HEADER = "x-forwarded-for";
export const FORWARDED_FOR_HOST_HEADER = "x-forwarded-host";
export const SECRET = "SHHHH!!";

export function decodeToken(token: string) {
  return jwt.verify(token, SECRET) as SharedTypes.IAccountToken;
}

export const hydraTokenMiddleware = (req: Request, res: Response, next: NextFunction): void => {
  if (req.url === "/access" || req.url.includes("/sessions/auth/token")) {
    return next();
  }

  if (req.hostname === env.WB_DOMAIN) {
    return next();
  }

  let forwardedHeader = REAL_IP_HEADER;
  if (req.headers[FORWARDED_FOR_HOST_HEADER] && !req.headers[REAL_IP_HEADER]) {
    forwardedHeader = FORWARDED_FOR_HOST_HEADER;
  }
  else if (req.headers[FORWARDED_FOR_HEADER] && !req.headers[REAL_IP_HEADER]) {
    forwardedHeader = FORWARDED_FOR_HEADER;
  }
  const clientIpHeader = req.headers[forwardedHeader] as string | undefined;

  if (clientIpHeader)
  {
    let resolvedIP = getRealIP(req);
    if (isIPAddress(resolvedIP.ip)) {
      req.realIp = resolvedIP.ip;
      req.requestForwarded = resolvedIP.isForwarded;
    }
    else {
      req.realIp = req.ip;
      req.requestForwarded = false;
    }
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

export function getRealIP(req: Request): { ip: string; isForwarded: boolean } {
  // Yeah, it's duplicated code
  // No, I don't care to fix it right now

  let forwardedHeader = REAL_IP_HEADER;
  if (req.headers[FORWARDED_FOR_HOST_HEADER] && !req.headers[REAL_IP_HEADER]) {
    forwardedHeader = FORWARDED_FOR_HOST_HEADER;
  }
  else if (req.headers[FORWARDED_FOR_HEADER] && !req.headers[REAL_IP_HEADER]) {
    forwardedHeader = FORWARDED_FOR_HEADER;
  }
  const clientIpHeader = req.headers[forwardedHeader] as string | undefined;
  let isForwarded = false;
  let tempRealIp = req.ip ?? "";

  if (clientIpHeader)
  {
    if (isIPAddress(clientIpHeader)) {
      tempRealIp = clientIpHeader;
      isForwarded = true;
    }
    else {
      tempRealIp = req.ip ?? "";
      isForwarded = false;
    }
  }
  return { ip: tempRealIp, isForwarded: isForwarded };
}

export function tryGetRealIP(req: Request): string {
  try {
    if (req.realIp)
    {
      return req.realIp;
    }
    else {
      return getRealIP(req).ip; // Ensure we get the real IP for name changes, not just the direct connection IP
    }
  } catch (error) {
    return req.ip || "";
  }
}
