import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import { MVSQueries } from "../interfaces/queries_types";
import { GleamiumData } from "../data/gleamium";
import { ToastData } from "../data/toast";
import { unlockAll, unlockAllCharacters } from "../data/characters";
import { getProfileForMatch } from "../services/profileService";
import { redisClient, RedisPlayerConnection, redisGetOnlinePlayers } from "../config/redis";
import { Types } from "mongoose";
import * as SharedTypes from "../types/shared-types";
import * as AuthUtils from "../utils/auth";
import { HYDRA_ACCESS_TOKEN, SECRET, decodeToken } from "../middleware/auth";
import { AccountToken, IAccountToken } from "../types/AccountToken";
import { isIPv4Address } from "../utils/garbagecan";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";

const serviceName = "Handlers.Profiles";
const logPrefix = `[${serviceName}]:`;

export async function handleProfiles_id_inventory(req: Request<{}, {}, {}, MVSQueries.Profiles_id_inventory_QUERY>, res: Response) {
    const account = AuthUtils.DecodeClientToken(req);

    let ip = "";
    let tempIp = "";
    try {
      var tryIP = AuthUtils.GetReqIP(req);
      tempIp = tryIP ? tryIP : "";
    }
    catch (error) {
      ip = account.current_ip;
      //return;
    }

    if (tempIp && undefined !== tempIp && tempIp !== "" && isIPv4Address(tempIp))
    {
      ip = tempIp;
    }
    else
    {
      ip = account.current_ip;
    }
    
    let rPlayerConnectionByIP = await redisClient.hGetAll(`connections:${ip}`) as unknown as RedisPlayerConnection;
    const aID = rPlayerConnectionByIP.id || account.id;
    logger.info(`${logPrefix} Inventory request for account ${aID} with IP ${ip}`);

    //res.send([...unlockAll(account.id), GleamiumData]);

    res.send([...unlockAll(aID), GleamiumData, ToastData]);
}

export async function handleProfiles_bulk(req: Request<{}, {}, { ids: string[] }, MVSQueries.Profiles_bulk_QUERY>, res: Response) {
  const account = req.token;
  const account_two = AuthUtils.DecodeClientToken(req);

  if (req.query.account_fields) {
    const ids = req.body.ids;
    let response = [];
    for (let id of ids) {
      const profile = await getProfileForMatch(id);
      if (profile) {
        response.push(profile);
      }
    }
    res.send(response);
    return;
  }
  logger.info(`${logPrefix} Other profile request`);

  const now = Math.floor(Date.now() / 1000);
  const results: any[] = [
    {
      updated_at: { _hydra_unix_date: now },
      account_id: account_two.id,
      created_at: { _hydra_unix_date: 1658510618 },
      last_login: { _hydra_unix_date: now },
      points: null,
      "server_data.SeasonalData.Season:SeasonTwo.Ranked.bEndOfSeasonRewardsGranted": true,
      user_segments: [],
      random_distribution: 0.09829278289973609,
      id: account_two.id,
    },
  ];

  // === HIDDEN: Online players in profiles disabled until DLL change is made ===
  /*
  try {
    const onlineIds = await redisGetOnlinePlayers();
    for (const pid of onlineIds) {
      if (pid === account_two.id) continue;
      results.push({
        updated_at: { _hydra_unix_date: now },
        account_id: pid,
        created_at: { _hydra_unix_date: now },
        last_login: { _hydra_unix_date: now },
        points: null,
        user_segments: [],
        random_distribution: Math.random(),
        id: pid,
      });
    }
  } catch (err) {
    logger.error(`${logPrefix} Error appending online players to profiles bulk: ${err}`);
  }
  */

  res.send(results);
}
