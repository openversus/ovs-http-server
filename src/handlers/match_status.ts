import { logger } from "../config/logger";
import { Request, Response } from "express";
import { IMatchStatus, IMatchStatusTimestamp } from "../interfaces/IMatchStatus";
import { redisClient, redisGetMatchConfig, redisPushDLLNotification } from "../config/redis";
import { processSetResult } from "../services/eloService";

const logPrefix = "[Handlers.MatchStatus]:";

// Events that are too noisy to log on every fire
const QUIET_EVENTS = new Set(["TickPerformance", "HeartBeat"]);

export async function handleMatchStatusUpdate(req: Request<{}, {}, IMatchStatus, {}>, res: Response): Promise<boolean> {
  const statusObject = (req.body as IMatchStatus) || ({} as IMatchStatus);
  const timestamp = (statusObject.Timestamp as IMatchStatusTimestamp) || ({} as IMatchStatusTimestamp);
  const event = statusObject.Event ?? "";
  const description = statusObject.Description ?? "";
  const matchId = statusObject.matchId ?? "";
  const key = statusObject.key ?? "";
  const numPlayers = statusObject.NumPlayers ?? 0;
  const playerId = statusObject.PlayerId ?? "";
  const playerIds = statusObject.PlayerIds ?? [];

  // Suppress noisy events from logs
  if (!QUIET_EVENTS.has(event)) {
    logger.info(
      `${logPrefix} Event=${event}, MatchId=${matchId}, NumPlayers=${numPlayers}, PlayerId=${playerId}, PlayerIds=${playerIds.join(",")}, Description=${description}`,
    );
  }

  // ── MatchStarted — set the match_started gate in Redis ──
  if (event === "MatchStarted" && matchId) {
    await redisClient.set(`match_started:${matchId}`, "1", { EX: 600 });
    logger.info(`${logPrefix} Set match_started:${matchId} — mid-game match_cancel DLL suppressed`);
  }

  // ── TerminatingError mid-match — flag as server crash ──
  if (event === "TerminatingError" && matchId) {
    const matchInProgress = await redisClient.get(`match_started:${matchId}`);
    if (matchInProgress) {
      await redisClient.set(`match_server_crash:${matchId}`, "1", { EX: 600 });
      logger.warn(`${logPrefix} Server crashed mid-match ${matchId} — ELO processing will be skipped`);
    }
  }

  // ── MatchEnded — clean up flags ──
  if (event === "MatchEnded" && matchId) {
    await redisClient.set(`match_ended:${matchId}`, "1", { EX: 600 });
    await redisClient.del(`match_started:${matchId}`);
  }

  // ── PlayerDisconnect — process ELO as dodge if match is active ──
  // This is the PRIMARY ELO trigger for rollback-detected disconnects.
  // WS disconnect handler still runs cleanup + DLL notifications but shares
  // the same dedup key, so whichever fires first processes ELO, the other skips.
  if (event === "PlayerDisconnect" && matchId && playerId && playerId !== "Unknown") {
    await handlePlayerDisconnectElo(matchId, playerId, playerIds);
  }

  return true;
}

/**
 * Process ELO for a rollback-detected player disconnect (dodge).
 * Skips if: custom game, match already ended, dedup key already claimed,
 * or server crashed. Shared dedup with WS handler ensures only one processes.
 */
async function handlePlayerDisconnectElo(
  matchId: string,
  disconnectedPlayerId: string,
  allPlayerIds: string[],
): Promise<void> {
  try {
    // Skip if match already ended normally (both players leave after game over)
    const matchEnded = await redisClient.get(`match_ended:${matchId}`);
    if (matchEnded) return;

    // Skip if the game already received a result via submit_end_of_match_stats.
    // This means the game finished naturally and the disconnect is normal post-game
    // cleanup, NOT a dodge. The flag is set ~5s before PlayerDisconnect fires.
    const gameResultReceived = await redisClient.get(`game_result_received:${matchId}`);
    if (gameResultReceived) {
      logger.info(`${logPrefix} Skipping ELO for PlayerDisconnect — game ${matchId} already has a result (normal post-game disconnect)`);
      return;
    }

    // Skip if server crashed (not fair to process ELO)
    const serverCrashed = await redisClient.get(`match_server_crash:${matchId}`);
    if (serverCrashed) return;

    // Get match config — need mode, team assignments, isCustomGame
    const matchConfig = await redisGetMatchConfig(matchId);
    if (!matchConfig || matchConfig.isCustomGame) return;

    // Find the disconnecting player in config to determine teams
    const leaver = matchConfig.players.find((p) => p.playerId === disconnectedPlayerId);
    if (!leaver || leaver.isSpectator) return;

    const winnerTeam = leaver.teamIndex === 0 ? 1 : 0;
    const team0Ids = matchConfig.players.filter((p) => p.teamIndex === 0 && !p.isSpectator).map((p) => p.playerId);
    const team1Ids = matchConfig.players.filter((p) => p.teamIndex === 1 && !p.isSpectator).map((p) => p.playerId);
    const winnerIds = winnerTeam === 0 ? team0Ids : team1Ids;
    const loserIds = winnerTeam === 0 ? team1Ids : team0Ids;

    // Resolve setId (player_ranked_set mapping, or matchId for game 1)
    const setId = await redisClient.get(`player_ranked_set:${disconnectedPlayerId}`) || matchId;

    // Match-level dedup check FIRST — catches the case where Player A's disconnect
    // already processed ELO and cleaned up player_ranked_set, then Player B alt-F4s
    // later with a different setId fallback. Without this, game 2+ of a set could
    // double-process because the set-level key uses a different setId.
    const matchAlreadyProcessed = await redisClient.get(`elo_processed:${matchId}`);
    if (matchAlreadyProcessed) return;

    // Set-level dedup — shared with WS disconnect handler and SSC check-in.
    // Whichever fires first processes ELO. The rest see "already done" and skip.
    const canProcess = await redisClient.set(`elo_processed_set:${setId}`, "rollback_disconnect", { NX: true, EX: 300 });
    if (canProcess !== "OK") return; // already processed by another path

    // Also claim match-level dedup
    await redisClient.set(`elo_processed:${matchId}`, "1", { NX: true, EX: 300 });

    // Resolve characters
    const chars = new Map<string, string>();
    const matchCharsRaw = await redisClient.get(`match_characters:${setId}`);
    const matchChars = matchCharsRaw ? JSON.parse(matchCharsRaw) : {};
    for (const pid of [...winnerIds, ...loserIds]) {
      if (matchChars[pid]) {
        chars.set(pid, matchChars[pid]);
      } else {
        try {
          const conn = await redisClient.hGetAll(`connections:${pid}`) as any;
          if (conn?.character) chars.set(pid, conn.character);
        } catch {}
      }
    }

    // Process ELO
    await processSetResult(winnerIds, loserIds, matchConfig.mode, [0, 0] as [number, number], winnerTeam, true, chars, matchId);
    logger.info(`${logPrefix} Dodge ELO processed via rollback PlayerDisconnect: ${disconnectedPlayerId} left match ${matchId} (set ${setId})`);

    // Publish FullRankUpdate so WS can push to connected clients
    await redisClient.publish("ranked_set:fullrankupdate", JSON.stringify({ playerIds: matchConfig.players.map((p) => p.playerId) }));

    // Flag the disconnecting player for auto-concede (so SSC check-in also knows)
    await redisClient.set(`ranked_disconnect:${disconnectedPlayerId}`, "1", { EX: 600 });

    // Clean up ranked set keys so next match doesn't link to this dead set
    const allSetPlayerIds = matchConfig.players.filter((p) => !p.isSpectator).map((p) => p.playerId);
    for (const pid of allSetPlayerIds) {
      await redisClient.del(`player_ranked_set:${pid}`);
    }
    if (setId !== matchId) {
      await redisClient.del(`ranked_set:${setId}`);
      await redisClient.del(`ranked_set_checkins:${setId}`);
    }

    // Push DLL match_cancel notification to remaining players if pre-game
    const matchStarted = await redisClient.get(`match_started:${matchId}`);
    if (!matchStarted) {
      for (const pid of allSetPlayerIds) {
        if (pid === disconnectedPlayerId) continue;
        try {
          await redisPushDLLNotification(pid, {
            type: "match_cancel",
            title: "Match Cancelled",
            message: "Opponent left the match",
            data: { matchId, reason: "opponent_dodge" },
            timestamp: Date.now(),
          });
          logger.info(`${logPrefix} Pushed match_cancel DLL notification to ${pid}`);
        } catch (err) {
          logger.error(`${logPrefix} Error pushing match_cancel to ${pid}: ${err}`);
        }
      }
    }

    logger.info(`${logPrefix} Cleaned up set ${setId} for ${allSetPlayerIds.length} players after dodge`);
  } catch (err) {
    logger.error(`${logPrefix} Error processing PlayerDisconnect ELO: ${err}`);
  }
}
