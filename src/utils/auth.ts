import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import * as SharedTypes from "../types/shared-types";
import { Types } from "mongoose";
import { HYDRA_ACCESS_TOKEN, SECRET, decodeToken } from "../middleware/auth";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { AccountToken, IAccountToken } from "../types/AccountToken";

const defaultPlayer = new PlayerTesterModel();
const IPRegexPattern = /^(::ffff:)?(?<matchip>(\d{1,3}\.){3}\d{1,3})$/;

export function GetReqIP(req: Request | Request<{}, {}, {}, {}> | Request<{}, {}, {}, any> | Request<any, any, any, any>): string | null {
  if (!req.ip && !req.token.current_ip) {
    return null;
  }

  let regexIsMatch = IPRegexPattern.test(req.ip || "");

  if (regexIsMatch) {
    var returnIP = IPRegexPattern.exec(req.ip || "") || null;
    if (returnIP && returnIP.groups) {
      return returnIP.groups.matchip;
    }
  }

  regexIsMatch = IPRegexPattern.test(req.token.current_ip || "");

  if (regexIsMatch) {
    var returnIP = IPRegexPattern.exec(req.token.current_ip || "") || null;
    if (returnIP && returnIP.groups) {
      return returnIP.groups.matchip;
    }
  }

  return null;
}

export function DecodeClientToken(req: Request<{}, {}, {}, {}>): IAccountToken
export function DecodeClientToken(req: Request<{}, {}, {}, any>): IAccountToken
export function DecodeClientToken(req: any): IAccountToken {
  let rawToken = req.headers[HYDRA_ACCESS_TOKEN] as string;
  let decodedToken = decodeToken(rawToken);
  return decodedToken;
}

export function GetIDFromToken(req: Request<{}, {}, {}, {}>): string
export function GetIDFromToken(req: Request<{}, {}, {}, any>): string
export function GetIDFromToken(req: any): string {
  let decodedToken = DecodeClientToken(req);
  return decodedToken.id;
}

export function GetIPFromToken(req: Request<{}, {}, {}, {}>): string
export function GetIPFromToken(req: Request<{}, {}, {}, any>): string
export function GetIPFromToken(req: any): string {
  let decodedToken = DecodeClientToken(req);
  return decodedToken.current_ip;
}

export async function GetPlayerFromToken(req: Request<{}, {}, {}, {}>): Promise<PlayerTester>
export async function GetPlayerFromToken(req: Request<{}, {}, {}, any>): Promise<PlayerTester>
export async function GetPlayerFromToken(req: any): Promise<PlayerTester> {
  let decodedToken = DecodeClientToken(req);
  let account = decodedToken;
  let player = defaultPlayer;

  try {
    const foundPlayer = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(account.id) });
    if (foundPlayer) {
      player = foundPlayer;
      logger.info(`Found player for id ${account.id} with IP ${account.current_ip}, getting inventory.`);
    }
  } catch (error) {
    logger.warn(`No player found for id ${account.id}, cannot get inventory.`);
  }

  // = await SharedTypes.PlayerTesterModel.findOne({ _id: new Types.ObjectId(account.id) });
  if (!player || player === defaultPlayer) {
    logger.warn(`No player found for id ${account.id}, cannot get inventory.`);
  }

  return player;
}
