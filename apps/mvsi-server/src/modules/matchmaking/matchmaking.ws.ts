import { env } from "@mvsi/env";
import { logger } from "@mvsi/logger";
import { encodeHydraWS, MAIN_WEBSOCKET, type MVSI_Websocket } from "../../websocket.elysia";
import { getActiveMatch } from "./matchmaking.service";
import {
  ACTIVEMATCH_END_CHANNEL,
  MATCHMAKING_CANCEL_CHANNEL,
  MATCHMAKING_COMPLETE_CHANNEL,
  MATCHMAKING_MATCH_FOUND_CHANNEL,
  MATCHMAKING_PERKS_LOCKED_CHANNEL,
  type MatchEndMessage,
  type MatchFoundChannelMessage,
  type MatchmakingCancelMessage,
  type MatchmakingCompleteMessage,
  type MatchmakingPerksLockMessage,
  MATCHMAKING_MATCH_TICK_CHANNEL,
} from "./matchmaking.types";
import { ObjectId } from "mongodb";

const subscriber = MAIN_WEBSOCKET.decorator.redisSub;
const clients = MAIN_WEBSOCKET.decorator.players;

subscriber.subscribe(MATCHMAKING_COMPLETE_CHANNEL, (message) => {
  const notification = JSON.parse(message) as MatchmakingCompleteMessage;
  handleMatchMakingComplete(notification);
});

subscriber.subscribe(MATCHMAKING_CANCEL_CHANNEL, (message) => {
  const notification = JSON.parse(message) as MatchmakingCancelMessage;
  handleCancelMatchMakingMessage(notification);
});

subscriber.subscribe(MATCHMAKING_MATCH_FOUND_CHANNEL, (message) => {
  const notification = JSON.parse(message) as MatchFoundChannelMessage;
  handleMatchFound(notification);
});

subscriber.subscribe(ACTIVEMATCH_END_CHANNEL, (message) => {
  const notification = JSON.parse(message) as MatchEndMessage;
  handleOnMatchEnd(notification);
});

subscriber.subscribe(MATCHMAKING_PERKS_LOCKED_CHANNEL, (message) => {
  const notification = JSON.parse(message) as MatchmakingPerksLockMessage;
  handleAllPerksLocked(notification);
});

subscriber.subscribe(MATCHMAKING_MATCH_TICK_CHANNEL, (message) => {
  const notification = JSON.parse(message) as string[];
  handleMatchTick(notification);
});

function handleMatchFound(notification: MatchFoundChannelMessage) {
  const { matchId, matchKey, playerIds, gameNotification } = notification;

  for (const playerId of playerIds) {
    const client = clients.get(playerId);
    if (client) {
      client.subscribe(matchId);
    }
  }

  const serverReadyMessage = {
    data: {
      MatchKey: matchKey,
      MatchID: matchId,
      Port: env.UDP_PORT,
      template_id: "GameServerReadyNotification",
      IPAddress: env.UDP_SERVER_IP,
    },
    payload: { match: { id: matchId }, custom_notification: "realtime" },
    header: "",
    cmd: "update",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(serverReadyMessage));

  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(gameNotification));
  logger.info(`Sent match notifications to all players for match ${matchId}`);

  const gameServerReadyMessage = {
    data: {},
    payload: {
      game_server_instance: {
        game_server_type_slug: "multiplay",
        port: env.UDP_PORT,
        owner_id: matchId,
        host: env.UDP_SERVER_IP,
        id: new ObjectId().toHexString(),
      },
      proxied_data: null,
    },
    header: "Your game server is ready to join.",
    cmd: "game-server-instance-ready",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(gameServerReadyMessage));
}

async function handleOnMatchEnd(notification: MatchEndMessage) {
  const connectedClients = getConnectedClients(notification.playersIds);
  if (!connectedClients) {
    return;
  }
  const matchId = notification.matchId;
  const activeMatch = await getActiveMatch(matchId);

  if (!activeMatch) {
    logger.error("Match not found");
    return;
  }

  for (const client of connectedClients) {
    const data = {
      data: {
        GameplayConfig: activeMatch.MatchConfig,
        template_id: "EndOfMatchPayload",
        ClientReturnData: {},
      },
      payload: {
        frm: {
          id: "internal-server",
          type: "server-api-key",
        },
        template: "realtime",
        account_id: client.data.account?.id,
        profile_id: client.data.account?.id,
      },
      header: "",
      cmd: "profile-notification",
    };
    client.data.sendHydra(client, data);
    setTimeout(() => {
      if (client.readyState === 1) {
        sendRematchDecline(client, matchId);
      }
    }, 1000);
  }
}

function sendRematchDecline(client: MVSI_Websocket, matchId: string) {
  const data = {
    data: {
      AccountId: client.data.account?.id,
      MatchId: matchId,
      template_id: "RematchDeclinedNotification",
    },
    payload: {
      frm: {
        id: "internal-server",
        type: "server-api-key",
      },
      template: "realtime",
      account_id: client.data.account?.id,
      profile_id: client.data.account?.id,
    },
    header: "",
    cmd: "profile-notification",
  };
  client.data.sendHydra(client, data);
}

async function handleMatchMakingComplete(notification: MatchmakingCompleteMessage) {
  const message = {
    data: {},
    payload: {
      result: {
        id: notification.resultId,
      },
      match: {
        id: notification.containerMatchId,
      },
      id: notification.matchmakingRequestId,
      state: 2,
    },
    header: "Matchmaking request completed!",
    cmd: "matchmaking-complete",
  };
  MAIN_WEBSOCKET.server?.publish(notification.matchmakingRequestId, encodeHydraWS(message));
  for (const playerId of notification.playerIds) {
    const client = clients.get(playerId);
    if (client) {
      client.unsubscribe(notification.matchmakingRequestId);
      client.subscribe(notification.containerMatchId);
    }
  }
}

export function handleMatchTick(matchmakingRequestIds: string[]) {
  for (const matchmakingRequestId of matchmakingRequestIds) {
    const data = {
      data: {},
      payload: {
        id: matchmakingRequestId,
        state: 2,
      },
      header: "matchmaking-tick",
      cmd: "matchmaking-tick",
    };
    MAIN_WEBSOCKET.server?.publish(matchmakingRequestId, encodeHydraWS(data));
  }
}

function handleCancelMatchMakingMessage(notification: MatchmakingCancelMessage) {
  const message = {
    data: {},
    payload: {
      id: notification.matchmakingRequestId,
      state: 3,
    },
    header: "Matchmaking request cancelled.",
    cmd: "matchmaking-cancel",
  };
  MAIN_WEBSOCKET.server?.publish(notification.matchmakingRequestId, encodeHydraWS(message));
  for (const playerId of notification.playersIds) {
    const client = clients.get(playerId);
    if (client) {
      client.unsubscribe(notification.matchmakingRequestId);
    }
  }
}

async function handleAllPerksLocked(notification: MatchmakingPerksLockMessage) {
  const connectedClients = getConnectedClients(notification.playerIds);
  if (!connectedClients) {
    return;
  }
  const matchId = notification.containerMatchIdKey.split(":")[1];
  const activeMatch = await getActiveMatch(matchId);
  if (!activeMatch) {
    logger.error("Match not found");
    return;
  }
  const message = {
    data: {
      GameplayConfig: activeMatch.MatchConfig,
      template_id: "PerksLockedNotification",
    },
    payload: {
      match: {
        id: activeMatch.MatchConfig.MatchId,
      },
      custom_notification: "realtime",
    },
    header: "",
    cmd: "update",
  };
  MAIN_WEBSOCKET.server?.publish(matchId, encodeHydraWS(message));
  logger.info(`Sent perks lock for match ${matchId} to all players`);
  for (const client of connectedClients) {
    if (client) {
      client.unsubscribe(matchId);
    }
  }
}

function getConnectedClients(clientPlayerIds: string[]) {
  const connectedClients = clientPlayerIds.reduce((filtered: MVSI_Websocket[], playerIds) => {
    const connectedClient = clients.get(playerIds);
    if (connectedClient) {
      filtered.push(connectedClient);
    }
    return filtered;
  }, []);
  if (connectedClients.length > 0) {
    return connectedClients;
  }
  return null;
}
