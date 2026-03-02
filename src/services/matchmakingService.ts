import {
  ON_CANCEL_MATCHMAKING,
  ON_MATCH_MAKER_STARTED_CHANNEL,
  ON_MATCH_MAKER_STARTED_NOTIFICATION,
  RedisCancelMatchMakingNotification,
  redisClient,
  redisGetPlayer,
  RedisMatchTicket,
  redisOnMatchMakerStarted,
  redisPushTicketToQueue,
  redisUpdatePlayerStatus,
  redisGetMatchTickets
} from "../config/redis";
import { logger } from "../config/logger";
import { MVSTime } from "../utils/date";

export enum MATCH_TYPES {
  ONE_V_ONE = "1v1",
  TWO_V_TWO = "2v2",
  FFA = "FFA",
}

export async function queueMatch(
  partyLeaderId: string,
  playerIds: string[],
  partyId: string,
  matchmakingRequestId: string,
  matchType: MATCH_TYPES,
): Promise<void> {

  const tickets = await redisGetMatchTickets(MATCH_TYPES.ONE_V_ONE);

  logger.info(`Current tickets in ${MATCH_TYPES.ONE_V_ONE} queue: ${tickets.length}`);
  logger.info(`Checking for duplicate players in party (${partyId}) against ${MATCH_TYPES.ONE_V_ONE} queue. Players in party: ${playerIds.join(", ")}`);
  if (tickets.length !== 0) {
    let queuedPlayers = tickets.reduce((acc: string[], ticket: RedisMatchTicket) => {
      return acc.concat(ticket.players.map(p => p.id));
    }, []);

    logger.info(`Players currently queued in ${MATCH_TYPES.ONE_V_ONE} queue: ${[...new Set(queuedPlayers)].join(", ")}`);

    if (queuedPlayers.some(p => playerIds.includes(p))) {
      logger.warn(`One or more players in party (${partyId}) are already queued for matchmaking. Players: ${playerIds.join(", ")}`);
      return;
    }
  }

  try {
    // Create a match ticket
    const ticket: ON_MATCH_MAKER_STARTED_NOTIFICATION = {
      created_at: MVSTime(new Date()),
      matchType,
      partyLeaderId,
      matchmakingRequestId,
      partyId,
      party_size: playerIds.length,
      players: await Promise.all(
        playerIds.map(async (p) => {
          const playerConfig = await redisGetPlayer(p);
          return {
            id: p,
            region: "MVSI",
            skill: 0,
            ip: playerConfig.ip,
          };
        }),
      ),
    };
    // Publish a message that a party has been queued
    await redisOnMatchMakerStarted(ticket);

    logger.info(
      `Party (${partyId}) matchmakingRequestId(${matchmakingRequestId}) has been added to ${matchType} matchmaking queue. Players (${playerIds.join(
        ",",
      )})`,
    );
  }
  catch (error) {
    logger.error(`Error queueing player: ${error}`);
    await cancelMatchmaking(partyLeaderId, matchmakingRequestId);
  }
}

export async function cancelMatchmaking(accountId: string, matchmakingId: string) {
  const notification: RedisCancelMatchMakingNotification = {
    playersIds: [accountId],
    matchmakingId,
  };
  await redisClient.publish(ON_CANCEL_MATCHMAKING, JSON.stringify(notification));
}

export async function cancelMatchmakingForAll(playerIds: string[], matchmakingId: string) {
  const notification: RedisCancelMatchMakingNotification = {
    playersIds: playerIds,
    matchmakingId,
  };
  await redisClient.publish(ON_CANCEL_MATCHMAKING, JSON.stringify(notification));
  logger.info(`Canceling matchmaking ${matchmakingId} for all players: ${playerIds.join(", ")}`);
}
