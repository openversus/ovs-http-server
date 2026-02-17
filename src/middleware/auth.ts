import { logger } from "../config/logger";
import { NextFunction, Request, Response } from "express";
import * as jwt from "jsonwebtoken";
import env from "../env/env";
import * as SharedTypes from "../types/shared-types";

declare global {
  namespace Express {
    interface Request {
      token: SharedTypes.IAccountToken;
      rawToken : string;
    }
  }
}

export const HYDRA_ACCESS_TOKEN = "x-hydra-access-token";
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
