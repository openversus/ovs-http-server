import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import {
  handle_ssc_set_lobby_mode,
  handleSsc_invoke_create_party_lobby,
  handleSsc_invoke_create_party,
  handleSsc_invoke_perks_get_all_pages,
  perks_set_page,
  set_lock_lobby_loadout,
  set_perks_absent,
  handle_ssc_update_player_preferences,
  handleSsc_invoke_invite_to_player_lobby,
  handleSsc_invoke_leave_player_lobby,
  handleSsc_invoke_join_party_lobby,
} from "./ssc";
import * as AuthUtils from "../utils/auth";
import { sendFriendRequest, removeFriend } from "../services/friendService";
import { redisClient, redisOnGameplayConfigNotified, redisMatchMakingComplete, redisUpdateMatch, RedisMatch, MATCH_FOUND_NOTIFICATION, RedisMatchTicket, redisPublishLobbyReturn } from "../config/redis";
import ObjectID from "bson-objectid";
import { randomBytes } from "crypto";
import { getRandomMapByType } from "../data/maps";
import { DeployInfo, getDefaultDeployInfo, useOnDemandRollback } from "../services/rollbackService";
import { processSetResult } from "../services/eloService";
import env from "../env/env";
import { sharedLobbyRouter } from "../modules/lobby/shared.routes";
import { customLobbyRouter } from "../modules/customLobby/lobby.routes";
import { handleRematchAccept, handleRematchDecline } from "../services/customLobbyService";

export const sscRouter = express.Router();

// Mount shared lobby routes FIRST (handles multi-path endpoints: leave, invite, ready, loadout, rematch)
sscRouter.use(sharedLobbyRouter);
// Then custom lobby routes (handles custom-game-specific endpoints)
sscRouter.use(customLobbyRouter);

// --- Existing handlers (party lobby, perks, loadout, friend system) ---

sscRouter.put("/ssc/invoke/game_install", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received game_install request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/lock_lobby_loadout", (req: Request, res: Response) => {
  logger.info(req.body);
  set_lock_lobby_loadout(req, res);
});

sscRouter.put("/ssc/invoke/perks_absent", (req: Request, res: Response) => {
  set_perks_absent(req, res);
});

sscRouter.put("/ssc/invoke/perks_set_character_page", (req: Request, res: Response) => {
  perks_set_page(req, res);
});

sscRouter.get("/ssc/invoke/perks_get_all_pages", async (req: Request, res: Response) => {
  await handleSsc_invoke_perks_get_all_pages(req, res);
});

// PartyManager::CreateParty() calls this — SEPARATE from create_party_lobby.
// PartyManager expects flat { MatchID } response (not nested in body.lobby).
sscRouter.put("/ssc/invoke/create_party", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party(req, res);
});

sscRouter.put("/ssc/invoke/create_party_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party_lobby(req, res);
});

sscRouter.put("/ssc/invoke/set_mode_for_lobby", async (req: Request, res: Response) => {
  await handle_ssc_set_lobby_mode(req, res);
});

sscRouter.put("/ssc/invoke/update_player_preferences", async (req: Request, res: Response) => {
  await handle_ssc_update_player_preferences(req, res);
});

sscRouter.put("/ssc/invoke/invite_to_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_invite_to_player_lobby(req, res);
});

sscRouter.put("/ssc/invoke/leave_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_leave_player_lobby(req, res);
});

sscRouter.put("/ssc/invoke/join_party_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_join_party_lobby(req, res);
});

// Rematch fallback (web-based custom lobby) — reached when customLobbyRouter's next() falls through
sscRouter.put("/ssc/invoke/rematch_accept", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      logger.info("[SSC.Routes]: Web lobby rematch accept from " + playerId);
      await handleRematchAccept(playerId);
    }
    res.send({ body: [], metadata: null, return_code: 0 });
  } catch (e) {
    logger.error("[SSC.Routes]: rematch_accept error: " + e);
    res.send({ body: [], metadata: null, return_code: 0 });
  }
});

sscRouter.put("/ssc/invoke/rematch_decline", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      logger.info("[SSC.Routes]: Web lobby rematch decline from " + playerId);
      const result = await handleRematchDecline(playerId);
      if (result.playerIds && result.playerIds.length > 0) {
        await redisClient.publish("custom_lobby_rematch_decline", JSON.stringify({ playerIds: result.playerIds }));
      }
    }
    res.send({ body: [], metadata: null, return_code: 0 });
  } catch (e) {
    logger.error("[SSC.Routes]: rematch_decline error: " + e);
    res.send({ body: [], metadata: null, return_code: 0 });
  }
});

// Ranked best-of-3 concede — player clicked CONCEDE between games
sscRouter.put("/ssc/invoke/match_set_concede", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    logger.info(`[SSC.Routes]: match_set_concede from player ${playerId}`);

    if (playerId) {
      const setId = await redisClient.get(`player_ranked_set:${playerId}`);
      if (setId) {
        const setRaw = await redisClient.get(`ranked_set:${setId}`);
        if (setRaw) {
          const setState = JSON.parse(setRaw);
          const allPlayerIds = [...new Set(setState.players.map((p: any) => p.playerId))] as string[];

          // Process ELO — concede = 2-0 loss for the conceding player's team
          const concederTeam = setState.players.find((p: any) => p.playerId === playerId)?.teamIndex;
          if (concederTeam !== undefined) {
            const scores = setState.scores || [0, 0];
            const winnerTeam = concederTeam === 0 ? 1 : 0;
            const team0Ids = setState.players.filter((p: any) => p.teamIndex === 0).map((p: any) => p.playerId);
            const team1Ids = setState.players.filter((p: any) => p.teamIndex === 1).map((p: any) => p.playerId);
            const winnerIds = winnerTeam === 0 ? team0Ids : team1Ids;
            const loserIds = winnerTeam === 0 ? team1Ids : team0Ids;
            try {
              const chars = await getPlayerCharacters([...winnerIds, ...loserIds]);
              await processSetResult(winnerIds, loserIds, setState.mode, scores as [number, number], winnerTeam, true, chars);

              // Send FullRankUpdate to all players after concede ELO processing
              await redisClient.publish("ranked_set:fullrankupdate", JSON.stringify({
                playerIds: allPlayerIds,
              }));
            } catch (e) {
              logger.error(`[SSC.Routes]: Error processing concede ELO: ${e}`);
            }
          }

          // Clean up ALL set state immediately
          for (const pid of allPlayerIds) {
            await redisClient.del(`player_ranked_set:${pid}`);
          }
          await redisClient.del(`ranked_set:${setId}`);

          // Send MatchSetLeaverNotification + empty OnGameplayConfigNotified to all players
          await redisClient.publish("ranked_set:leaver", JSON.stringify({
            playerIds: allPlayerIds,
            leaverPlayerId: playerId,
            matchId: setId,
          }));
          logger.info(`[SSC.Routes]: Player ${playerId} conceded set ${setId}, sending MatchSetLeaverNotification`);
        }
      }
    }
  } catch (e) {
    logger.error(`[SSC.Routes]: match_set_concede error: ${e}`);
  }
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// Ranked best-of-3 check-in — player clicked READY between games
sscRouter.put("/ssc/invoke/match_set_checkin", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    logger.info(`[SSC.Routes]: match_set_checkin from player ${playerId}`);

    if (playerId) {
      await handleRankedSetCheckin(playerId);
    }
  } catch (e) {
    logger.error(`[SSC.Routes]: match_set_checkin error: ${e}`);
  }
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// Ranked best-of-3 absent — player timed out; treat as auto-ready
sscRouter.put("/ssc/invoke/match_set_absent", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    logger.info(`[SSC.Routes]: match_set_absent from player ${playerId} (treating as auto-ready)`);

    if (playerId) {
      await handleRankedSetCheckin(playerId);
    }
  } catch (e) {
    logger.error(`[SSC.Routes]: match_set_absent error: ${e}`);
  }
  res.send({ body: {}, metadata: null, return_code: 0 });
});

async function getPlayerCharacters(playerIds: string[]): Promise<Map<string, string>> {
  const chars = new Map<string, string>();
  for (const pid of playerIds) {
    try {
      const conn = await redisClient.hGetAll(`connections:${pid}`);
      if (conn?.character) chars.set(pid, conn.character);
    } catch {}
  }
  return chars;
}

async function handleRankedSetCheckin(playerId: string) {
  const setId = await redisClient.get(`player_ranked_set:${playerId}`);
  if (!setId) {
    logger.warn(`[SSC.Routes]: No active ranked set for player ${playerId}`);
    return;
  }

  const setRaw = await redisClient.get(`ranked_set:${setId}`);
  if (!setRaw) {
    logger.warn(`[SSC.Routes]: Ranked set ${setId} not found in Redis`);
    return;
  }

  const setState = JSON.parse(setRaw);

  // Avoid duplicate check-ins
  if (setState.checkins.includes(playerId)) {
    logger.info(`[SSC.Routes]: Player ${playerId} already checked in for set ${setId}`);
    return;
  }

  setState.checkins.push(playerId);
  await redisClient.set(`ranked_set:${setId}`, JSON.stringify(setState), { EX: 600 });

  // Get unique player IDs from team entries
  const allPlayerIds = [...new Set(setState.players.map((p: any) => p.playerId))] as string[];
  logger.info(`[SSC.Routes]: Set ${setId} check-in: ${setState.checkins.length}/${allPlayerIds.length}`);

  // Notify all players about the check-in so their UI updates
  await redisClient.publish("ranked_set:checkin", JSON.stringify({
    playerIds: allPlayerIds,
    checkedInPlayer: playerId,
    checkins: setState.checkins,
    totalPlayers: allPlayerIds.length,
    setId,
  }));

  if (setState.checkins.length >= allPlayerIds.length) {
    // Dedup: prevent double match creation from checkin + absent race
    const dedup = await redisClient.set(`set_match_dedup:${setId}:${setState.gamesPlayed}`, "1", { NX: true, EX: 30 });
    if (dedup !== "OK") {
      logger.info(`[SSC.Routes]: Match already created for set ${setId} game ${setState.gamesPlayed}, skipping`);
      return;
    }

    // All players checked in or timed out — check if set is over
    const scores = setState.scores || [0, 0];
    const setOver = setState.conceded || scores[0] >= 2 || scores[1] >= 2 || setState.gamesPlayed >= 3;

    if (setOver) {
      // Set is over — process ELO, clean up, and send MatchSetLeaverNotification
      const reason = setState.conceded ? "concede" : `score ${scores[0]}-${scores[1]}`;
      logger.info(`[SSC.Routes]: Set ${setId} is over (${reason}), processing ELO and ending set`);

      // Determine winner/loser teams from players array
      const team0Ids = setState.players.filter((p: any) => p.teamIndex === 0).map((p: any) => p.playerId);
      const team1Ids = setState.players.filter((p: any) => p.teamIndex === 1).map((p: any) => p.playerId);
      const winnerTeam = scores[0] > scores[1] ? 0 : 1;
      const winnerIds = winnerTeam === 0 ? team0Ids : team1Ids;
      const loserIds = winnerTeam === 0 ? team1Ids : team0Ids;

      // Process set-based ELO
      try {
        const chars = await getPlayerCharacters([...winnerIds, ...loserIds]);
        await processSetResult(
          winnerIds, loserIds, setState.mode,
          scores as [number, number], winnerTeam,
          setState.conceded || false, chars,
        );
      } catch (e) {
        logger.error(`[SSC.Routes]: Error processing set ELO: ${e}`);
      }

      for (const pid of allPlayerIds) {
        await redisClient.del(`player_ranked_set:${pid}`);
      }
      await redisClient.del(`ranked_set:${setId}`);
      // Send MatchSetLeaverNotification after SSC response
      const leaverPlayer = setState.concedingPlayer || allPlayerIds[0];
      setTimeout(async () => {
        await redisClient.publish("ranked_set:leaver", JSON.stringify({
          playerIds: allPlayerIds,
          leaverPlayerId: leaverPlayer,
          matchId: setId,
        }));
        logger.info(`[SSC.Routes]: Sent MatchSetLeaverNotification for set ${setId}`);
      }, 500);
    } else {
      // Set continues — create next match
      logger.info(`[SSC.Routes]: All players checked in for set ${setId}, creating next match (game ${setState.gamesPlayed + 1}/3)`);
      await createNextSetMatch(setId, setState);
    }
  }
}

async function createNextSetMatch(setId: string, setState: any) {
  const matchId = ObjectID().toHexString();
  const resultId = ObjectID().toHexString();
  const matchmakingRequestId = ObjectID().toHexString();
  const rollbackPort = DeployInfo.getRandomRollbackPort();

  const allPlayerIds = [...new Set(setState.players.map((p: any) => p.playerId))] as string[];

  // Create match ticket
  const ticket: RedisMatchTicket = {
    party_size: allPlayerIds.length,
    players: allPlayerIds.map((id: string) => ({
      id,
      skill: 0,
      region: "local",
      partyId: matchId,
    })),
    created_at: Date.now(),
    partyId: matchId,
    matchmakingRequestId,
  };

  const match: RedisMatch = {
    matchId,
    resultId,
    tickets: [ticket],
    status: "pending",
    createdAt: Date.now(),
    matchType: setState.mode,
    totalPlayers: allPlayerIds.length,
    rollbackPort,
  };
  await redisUpdateMatch(matchId, match);

  // Select new map
  const map = await getRandomMapByType(setState.mode, matchId);

  // Deploy rollback server if on-demand
  if (useOnDemandRollback) {
    const deployInfo = getDefaultDeployInfo();
    deployInfo.port = rollbackPort;
    deployInfo.entrypoint = deployInfo.entrypoint.replace("CHANGEMEDEFAULTPORT", deployInfo.port.toString());
    deployInfo.ovs_server = env.OVS_SERVER;
    const isDeployed = DeployInfo.Deploy(deployInfo);
    if (!isDeployed) {
      logger.error(`[SSC.Routes]: Failed to deploy rollback server for set match ${matchId}`);
    }
  }

  // Reuse same player team assignments from the original match
  const notification: MATCH_FOUND_NOTIFICATION = {
    players: setState.players,
    matchId,
    matchKey: randomBytes(32).toString("base64"),
    map,
    mode: setState.mode,
    rollbackPort,
  };

  await redisOnGameplayConfigNotified(notification);
  await redisMatchMakingComplete(matchId, matchmakingRequestId, allPlayerIds);

  // Update set state: reset checkins, update matchId mappings
  setState.checkins = [];
  await redisClient.set(`ranked_set:${setId}`, JSON.stringify(setState), { EX: 600 });

  // Update player→set mappings (keep pointing to original setId)
  for (const pid of allPlayerIds) {
    await redisClient.set(`player_ranked_set:${pid}`, setId, { EX: 600 });
  }

  logger.info(`[SSC.Routes]: Created set match ${matchId} (game ${setState.gamesPlayed + 1}/3) on map ${map}`);
}

// Stub handlers for party-related SSC endpoints
sscRouter.put("/ssc/invoke/cancel_party_invite", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received cancel_party_invite request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/decline_party_invite", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received decline_party_invite request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/autoparty_join", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received autoparty_join request");
  logger.info(req.body);
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// set_lobby_joinable handled below with set_lobby_not_joinable

sscRouter.put("/ssc/invoke/set_lobby_not_joinable", async (req: Request, res: Response) => {
  // When matchmaking starts, clear pending invites so stale joins fail
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobbyId = req.body?.LobbyId;
    if (lobbyId) {
      // Mark lobby as not joinable by setting a flag
      const raw = await redisClient.get(`lobby:${lobbyId}`);
      if (raw) {
        const state = JSON.parse(raw);
        state.joinable = false;
        await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(state), { EX: 3600 });
      }
      logger.info(`[SSC.Routes]: set_lobby_not_joinable: Lobby ${lobbyId} marked not joinable`);
    }
  } catch {}
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/set_lobby_joinable", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobbyId = req.body?.LobbyId;
    if (lobbyId) {
      const raw = await redisClient.get(`lobby:${lobbyId}`);
      if (raw) {
        const state = JSON.parse(raw);
        state.joinable = true;
        await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(state), { EX: 3600 });
      }
      logger.info(`[SSC.Routes]: set_lobby_joinable: Lobby ${lobbyId} marked joinable`);
    }
  } catch {}
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/update_party_game_modes", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received update_party_game_modes request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

// --- Friend system: send_profile_notification ---
// The game calls this SSC when the player uses the native friend UI to:
//   - Send a friend request (Type: "friend_request")
//   - Remove a friend (Type: "unfriend")
// Body typically contains: { TargetAccountId, Type, ... }
sscRouter.put("/ssc/invoke/send_profile_notification", async (req: Request, res: Response) => {
  try {
    const body = req.body;
    const account = AuthUtils.DecodeClientToken(req);
    const myId = account.id;

    logger.info(`[SSC.Routes]: send_profile_notification from ${myId}: ${JSON.stringify(body)}`);

    const targetAccountId = body.TargetAccountId || body.targetAccountId || body.target_account_id || body.AccountId || body.accountId;
    const notifType = body.Type || body.type || body.template_id || "";

    if (!targetAccountId) {
      logger.warn("[SSC.Routes]: send_profile_notification missing TargetAccountId");
      res.send({ body: {}, metadata: null, return_code: 200 });
      return;
    }

    // Get player usernames from Redis connections
    const myConn = await redisClient.hGetAll(`connections:${myId}`);
    const targetConn = await redisClient.hGetAll(`connections:${targetAccountId}`);
    const myUsername = myConn?.username || myConn?.hydraUsername || "Unknown";
    const targetUsername = targetConn?.username || targetConn?.hydraUsername || "Unknown";

    if (notifType.toLowerCase().includes("unfriend") || notifType.toLowerCase().includes("remove")) {
      // Remove friend — check BEFORE "friend" since "unfriend" contains "friend"
      const result = await removeFriend(myId, targetAccountId);
      logger.info(`[SSC.Routes]: Remove friend result: ${JSON.stringify(result)}`);
    } else if (notifType.toLowerCase().includes("friend") || notifType.toLowerCase().includes("request")) {
      // Friend request
      const result = await sendFriendRequest(myId, myUsername, targetAccountId, targetUsername);
      logger.info(`[SSC.Routes]: Friend request result: ${JSON.stringify(result)}`);
    } else {
      // Unknown notification type — log it
      logger.info(`[SSC.Routes]: Unknown profile notification type "${notifType}", ignoring`);
    }

    res.send({ body: {}, metadata: null, return_code: 200 });
  } catch (error) {
    logger.error(`[SSC.Routes]: Error in send_profile_notification: ${error}`);
    res.send({ body: {}, metadata: null, return_code: 200 });
  }
});