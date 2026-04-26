import express, { Request, Response } from "express";
import { MVSQueries } from "../interfaces/queries_types";
import * as AuthUtils from "../utils/auth";
import * as KitchenSink from "../utils/garbagecan";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";

export async function handleSocial_me_blocked(req: Request<{}, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;
  const playerUsername = account.username;
  
  if (!aID) {
    res.status(200).send({ total: 0, page: 1, page_size: 20, results: [] });
    return;
  }

  let mongoPlayer = await PlayerTesterModel.findOne({ id: aID });
  let blockedPlayers: string[] = mongoPlayer?.blockedPlayers ?? [];
  let totalBlocked = blockedPlayers.length;

  // For simplicity, we are not implementing pagination here since the blocked players list is expected to be small.

  res.send({ total: totalBlocked, page: 1, page_size: 20, results: blockedPlayers });

  //res.send({ total: 0, page: 1, page_size: 20, results: [] });
}
