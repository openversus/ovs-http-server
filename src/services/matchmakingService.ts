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
} from "../config/redis";
import { logger } from "../config/logger";
import { MVSTime } from "../utils/date";
import { getOrCreateRating } from "./eloService";
import { leaveLobby } from "./customLobbyService";

export enum MATCH_TYPES {
  ONE_V_ONE = "1v1",
  TWO_V_TWO = "2v2",
  FFA = "FFA",
}

/**
 * Returns the base mode from a match type.
 */
export function getBaseMode(matchType: string): string {
  return matchType;
}

export async function queueMatch(
  partyLeaderId: string,
  playerIds: string[],
  partyId: string,
  matchmakingRequestId: string,
  matchType: MATCH_TYPES,
): Promise<void> {
  try {
    // Flush players from any custom lobby they're in before queuing
    for (const pid of playerIds) {
      try {
        await leaveLobby(pid);
      } catch {
        // ignore — player may not be in a custom lobby
      }
    }

    // Look up each player's ELO for skill-based matchmaking
    const eloField = matchType === "1v1" ? "elo_1v1" : "elo_2v2";

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
          // Look up player's actual ELO from MongoDB
          let skill = 0;
          try {
            const rating = await getOrCreateRating(p);
            skill = rating[eloField];
          } catch (err) {
            logger.warn(`Could not fetch ELO for player ${p}, defaulting to 0: ${err}`);
          }
          return {
            id: p,
            region: "MVSI",
            skill,
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
