import express, { Request, Response } from "express";
import { MVSQueries } from "../interfaces/queries_types";
import { redisClient, redisGetOnlinePlayers, RedisPlayerConnection } from "../config/redis";
import { logger } from "../config/logger";
import * as AuthUtils from "../utils/auth";

const serviceName = "Handlers.Friends";
const logPrefix = `[${serviceName}]:`;

export async function handleFriends_me(req: Request<{}, {}, {}, MVSQueries.Friends_me_QUERY>, res: Response) {
  // === HIDDEN: Online friends list disabled until DLL change is made ===
  // Once the DLL supports proper friend invites, re-enable the block below.
  res.send({
    total: 0,
    page: 1,
    page_size: 1000,
    results: [],
  });

  /*
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;

    const onlinePlayerIds = await redisGetOnlinePlayers();

    const results: any[] = [];

    for (const playerId of onlinePlayerIds) {
      if (playerId === myId) continue;

      const connection = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;
      if (!connection || !connection.id) continue;

      results.push({
        created_at: new Date().toISOString(),
        account: {
          public_id: connection.public_id || playerId,
          username: connection.username || connection.hydraUsername || "Unknown",
          avatar: {
            name: "MultiVersus",
            image_url: "https://prod-network-images.wbagora.com/network/account-wbgames-com/multiversus-shaggy.jpg",
          },
          presence_state: 1,
        },
      });
    }

    logger.info(`${logPrefix} Returning ${results.length} online players as friends for player ${myId}`);

    res.send({
      total: results.length,
      page: 1,
      page_size: 1000,
      results,
    });
  } catch (error) {
    logger.error(`${logPrefix} Error fetching online players: ${error}`);
    res.send({
      total: 0,
      page: 1,
      page_size: 1000,
      results: [],
    });
  }
  */
}

export async function handleFriends_me_invitations_incoming(
  req: Request<{}, {}, {}, MVSQueries.Friends_me_invitations_incoming_QUERY>,
  res: Response,
) {
  res.send({
    total: 0,
    page: 1,
    page_size: 1000,
    results: [],
  });
}

export async function handleFriends_me_invitations_outgoing(
  req: Request<{}, {}, {}, MVSQueries.Friends_me_invitations_outgoing_QUERY>,
  res: Response,
) {
  res.send({
    total: 0,
    page: 1,
    page_size: 1000,
    results: [],
  });
}
