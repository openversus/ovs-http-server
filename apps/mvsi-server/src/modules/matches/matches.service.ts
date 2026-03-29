import { logger } from "@mvsi/logger";
import { redisClient } from "@mvsi/redis";
import { ObjectId } from "mongodb";
import { getRandomMapByType } from "../../data/maps";
import { getLobby } from "../lobby/lobby.service";
import type { CustomLobby } from "../lobby/lobby.types";
import { getActiveMatch, notifyActiveMatchCreated } from "../matchmaking/matchmaking.service";
import type { ActiveMatch, GameplayConfig } from "../matchmaking/matchmaking.types";
import { broadcastNotificationToTopic } from "../notifications/notifications.utils";

const MATCH_KEY = (matchId: string) => `match:${matchId}`;

/**
 * Lua script that atomically:
 * 1. Checks the player belongs to the match
 * 2. Adds the player to the RematchAccepted set (prevents double-counting)
 * 3. Counts real (non-bot) players in the match
 * 4. Returns [newCount, realPlayerCount] so the caller knows if all accepted
 *
 * KEYS[1] = match:{matchId}
 * ARGV[1] = accountId
 */
const LUA_REMATCH_ACCEPT = `
  local matchJson = redis.call('JSON.GET', KEYS[1], '$')
  if not matchJson then
    return {-1, 0}
  end

  local match = cjson.decode(matchJson)
  local matchData = match[1]
  local players = matchData.GameplayConfig.GameplayConfig.Players

  -- Verify player is in the match
  local found = false
  for id, _ in pairs(players) do
    if id == ARGV[1] then
      found = true
      break
    end
  end
  if not found then
    return {-2, 0}
  end

  -- Initialize RematchAccepted array if missing
  if not matchData.RematchAccepted then
    redis.call('JSON.SET', KEYS[1], '$.RematchAccepted', '[]')
  end

  -- Check if player already accepted (read array)
  local acceptedJson = redis.call('JSON.GET', KEYS[1], '$.RematchAccepted')
  local acceptedArr = cjson.decode(acceptedJson)[1]
  for _, v in ipairs(acceptedArr) do
    if v == ARGV[1] then
      -- Already accepted, return current counts without double-adding
      local realCount = 0
      for _, cfg in pairs(players) do
        if not cfg.bIsBot then
          realCount = realCount + 1
        end
      end
      return {#acceptedArr, realCount}
    end
  end

  -- Add player to accepted list
  redis.call('JSON.ARRAPPEND', KEYS[1], '$.RematchAccepted', '"' .. ARGV[1] .. '"')

  -- Count real players
  local realCount = 0
  for _, cfg in pairs(players) do
    if not cfg.bIsBot then
      realCount = realCount + 1
    end
  end

  return {#acceptedArr + 1, realCount}
`;

export async function toastPlayer(accountId: string, _matchId: string, toasteeId: string) {
  await broadcastNotificationToTopic({
    topic: toasteeId,
    data: {
      data: {
        ToasterAccountID: accountId,
        template_id: "ToastReceivedNotification",
      },
      payload: {
        frm: {
          id: "internal-server",
          type: "server-api-key",
        },
        template: "realtime",
        account_id: toasteeId,
        profile_id: toasteeId,
      },
      header: "",
      cmd: "profile-notification",
    },
  });
}

export async function rematchDeclined(accountId: string, matchId: string) {
  const match = await getActiveMatch(matchId);
  if (!match) {
    return;
  }
  const playerFromMatch = Object.keys(match.GameplayConfig.GameplayConfig.Players).includes(
    accountId,
  );
  if (!playerFromMatch) {
    return;
  }
  for (const playerKey in match.GameplayConfig.GameplayConfig.Players) {
    const player = match.GameplayConfig.GameplayConfig.Players[playerKey];
    await broadcastNotificationToTopic({
      topic: player.AccountId,
      data: {
        data: {
          AccountId: player.AccountId,
          MatchId: matchId,
          template_id: "RematchDeclinedNotification",
        },
        payload: {
          frm: {
            id: "internal-server",
            type: "server-api-key",
          },
          template: "realtime",
          account_id: player.AccountId,
          profile_id: player.AccountId,
        },
        header: "",
        cmd: "profile-notification",
      },
    });
  }
}

export async function rematchAccepted(accountId: string, matchId: string) {
  const result = (await redisClient.eval(LUA_REMATCH_ACCEPT, {
    keys: [MATCH_KEY(matchId)],
    arguments: [accountId],
  })) as number[];

  const [acceptedCount, realPlayerCount] = result;

  if (acceptedCount === -1) {
    logger.warn(`Rematch accept failed: match ${matchId} not found`);
    return;
  }
  if (acceptedCount === -2) {
    logger.warn(`Rematch accept failed: player ${accountId} not in match ${matchId}`);
    return;
  }

  // Get match data for broadcasting
  const match = await getActiveMatch(matchId);
  if (!match) {
    return;
  }

  for (const playerKey in match.GameplayConfig.GameplayConfig.Players) {
    const player = match.GameplayConfig.GameplayConfig.Players[playerKey];
    if (player.bIsBot) continue;
    await broadcastNotificationToTopic({
      topic: player.AccountId,
      data: {
        data: {
          AccountId: accountId,
          MatchId: matchId,
          template_id: "RematchAcceptedNotification",
        },
        payload: {
          frm: {
            id: "internal-server",
            type: "server-api-key",
          },
          template: "realtime",
          account_id: player.AccountId,
          profile_id: player.AccountId,
        },
        header: "",
        cmd: "profile-notification",
      },
    });
  }

  if (acceptedCount >= realPlayerCount) {
    await startRematch(match);
  }
}

async function startRematch(activeMatch: ActiveMatch) {
  const prevMatch = activeMatch.GameplayConfig.GameplayConfig;

  const lobby = await getLobby(activeMatch.fromLobbyId);
  if (!lobby) {
    logger.error(`Lobby ${activeMatch.fromLobbyId} not found for rematch`);
    return;
  }
  let map: string | undefined;
  if ((lobby as CustomLobby).Maps) {
    const customLobby = lobby as CustomLobby;
    const randomMap =
      customLobby.Maps[Math.floor(Math.random() * customLobby.Maps.length)] ?? customLobby.Maps[0];
    map = randomMap.Map;
  } else {
    map = getRandomMapByType(prevMatch.ModeString);
  }

  const newMatchId = new ObjectId().toHexString();
  const gameplayConfig: GameplayConfig = {
    ...activeMatch.GameplayConfig,
    GameplayConfig: {
      ...prevMatch,
      Map: map || prevMatch.Map,
      MatchId: newMatchId,
      Created: new Date(),
    },
  };

  await notifyActiveMatchCreated(activeMatch.template, activeMatch.fromLobbyId, gameplayConfig);
}
