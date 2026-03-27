import { getActiveMatch } from "../matchmaking/matchmaking.service";
import { broadcastNotificationToTopic } from "../notifications/notifications.utils";

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
    if (player.AccountId === accountId) {
      continue;
    }
    await broadcastNotificationToTopic({
      topic: player.AccountId,
      data: {
        data: {
          AccountId: player.AccountId,
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
}
