import { WebSocket, WebSocketServer } from "ws";
import { MVSHTTPServer } from "./server";
import { randomUUID, randomInt } from "crypto";
import { HydraDecoder, HydraEncoder } from "mvs-dump";
import { buffer } from "stream/consumers";
import { decodeToken } from "./middleware/auth";
//import { AccountToken } from "./handlers";
import * as SharedTypes from "./types/shared-types";
import * as RollbackProcess from "./utils/processes";
import * as KitchenSink from "./utils/garbagecan";
//import { exec, spawn, spawnSync, SpawnOptions } from "child_process";

import {
  ALL_PERKS_LOCKED_CHANNEL,
  initRedisSubscriber,
  MATCH_FOUND_NOTIFICATION,
  ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL,
  GAME_SERVER_INSTANCE_READY_CHANNEL,
  ON_MATCH_MAKER_STARTED_CHANNEL,
  ON_MATCH_MAKER_STARTED_NOTIFICATION,
  redisClient,
  redisGetPlayerPerk,
  RedisPlayerConfig,
  RedisGameServerInstanceReadyNotification,
  RedisClient,
  redisGetAllPlayersEquippedCosmetics,
  redisGetPlayers,
  RedisAllPerksLockedNotification,
  MATCHMAKING_COMPLETE_CHANNEL,
  RedisMatchMakingCompleteNotification,
  redisDeletePlayerKeys,
  redisPushTicketToQueue,
  redisUpdatePlayerStatus,
  redisOnMatchMakerStarted,
  redisPopMatchTicketsFromQueue,
  ON_LOBBY_MODE_UPDATED,
  RedisOnGameModeUpdatedNotification as RedisOnLobbyModeUpdatedNotification,
  ON_CANCEL_MATCHMAKING,
  RedisCancelMatchMakingNotification,
  ON_END_OF_MATCH,
  RedisMatchEndNotification,
  redisGetGamePort,
  redisDeleteConnectionKeysByIp,
  redisSetPlayerConnectionCosmetics,
  RedisPlayerConnection,
  redisAddOnlinePlayer,
  redisRemoveOnlinePlayer,
  PARTY_INVITE_CHANNEL,
  RedisPartyInviteNotification,
  PLAYER_JOINED_LOBBY_CHANNEL,
  RedisPlayerJoinedLobbyNotification,
  LOBBY_REJOIN_CHANNEL,
  RedisLobbyRejoinNotification,
  redisCleanupPlayerLobby,
  redisGetPlayerLobby,
  redisGetLobbyState,
  redisSaveLobbyState,
  redisSavePlayerLobby,
  redisDeleteLobbyState,
  redisDeletePlayerLobby,
  redisPublishLobbyRejoin,
  redisGetMatchTickets,
  TOAST_RECEIVED_CHANNEL,
  RedisToastNotification,
  redisGetMatchConfig,
  PLAYER_LOADOUT_LOCKED_CHANNEL,
  RedisPlayerLoadoutLockedNotification,
  LOBBY_RETURN_CHANNEL,
  RedisLobbyReturnNotification,
  LOBBY_TRANSITION_CHANNEL,
  RedisLobbyTransitionNotification,
  FRIEND_REQUEST_WS_CHANNEL,
  RedisFriendRequestWSNotification,
  DLLNotification,
  redisPushDLLNotification,
  RedisLobbyState,
  redisUpdatePartyKeyLobby,
  redisSetPendingJoinLobby,
  // PARTY_MEMBER_JOIN_CHANNEL — moved to AccelByteLobbyWsService
} from "./config/redis";
import { Server } from "https";
import { Server as HttpServer } from "http";
import { GAME_SERVER_PORT } from "./game/udp";
import { logger, logwrapper, BE_VERBOSE } from "./config/logger";
import { MVSTime } from "./utils/date";
import { getCustomLobbyForMatch, handleCustomLobbyMatchEnd, leaveLobby } from "./services/customLobbyService";
import { leaveLobby as leaveSscCustomLobby, getSscCustomLobbyForMatch, handleSscCustomLobbyMatchEnd } from "./modules/customLobby/lobby.service";
import { createLobby, LOBBY_MODES } from "./services/lobbyService";
import { getMapHazards } from "./data/maps";
import ObjectID from "bson-objectid";
import { RedisClientType } from "@redis/client";
import env from "./env/env";
import { Cosmetics, TauntSlotsClass, defaultTaunts, IDefaultTaunts } from "./database/Cosmetics";
import { getEquippedCosmetics } from "./services/cosmeticsService";
import { cancelMatchmakingForAll } from "./services/matchmakingService";
import { processMatchLeave, getOrCreateRating, eloToTierDivision, processSetResult, getPlayerRank } from "./services/eloService";
import { PlayerTesterModel } from "./database/PlayerTester";
import { PlayerStatsModel } from "./database/PlayerStats";
import { INVENTORY_DEFINITIONS } from "./data/inventoryDefs";

const serviceName: string = "WebSocket";
const logPrefix = `[${serviceName}]:`;

export class WebSocketPlayer {
  init: boolean = false;
  ws: WebSocket;
  deleted?: boolean;
  account: SharedTypes.IAccountToken | undefined;
  matchTick: NodeJS.Timeout | undefined;
  matchTimeout: NodeJS.Timeout | undefined;
  matchConfig?: GameNotification;
  ticket?: ON_MATCH_MAKER_STARTED_NOTIFICATION;
  ip: string;

  constructor(_ws: WebSocket, ip: string) {
    this.ip = ip;
    logger.info(ip);
    this.ws = _ws;
  }

  send(data: Object) {
    const encoder = new HydraEncoder(true);
    encoder.encodeValue(data);
    if (!this.deleted) {
      this.ws.send(encoder.returnValue());
    }
  }

  sendRaw(data: Buffer<ArrayBuffer>) {
    if (!this.deleted) {
      this.ws.send(data);
    }
  }

  /**
   * Send a text-format message (AccelByte YAML-like format) through the Hydra WS.
   * Some games check both binary and text messages on the same connection.
   */
  sendText(text: string) {
    if (!this.deleted) {
      this.ws.send(text);
    }
  }
}

interface MatchData {
  MatchId: string;
  GameplayConfig: GameplayConfig;
  template_id: string;
}

interface ArenaModeInfo {
  FaceoffWaitTime: number; // int32 seconds (UE5 FArenaModeInfo.FaceoffWaitTime @ +0x0)
  ShopTime: number;        // int32 seconds (FArenaModeInfo.ShopTime @ +0x4)
  bReadyToStart: boolean;  //               (FArenaModeInfo.bReadyToStart @ +0x8)
}

interface GameplayConfig {
  ArenaModeInfo: ArenaModeInfo | null | string;
  RiftNodeId: string;
  ScoreEvaluationRule: string;
  bIsPvP: boolean;
  ScoreAttributionRule: string;
  MatchDurationSeconds: number;
  Created: {
    _hydra_unix_date: number;
  };
  EventQueueSlug: string;
  bModeGrantsProgress: boolean;
  TeamData: any[];
  Spectators: object;
  bIsRanked: boolean;
  bIsCustomGame: boolean;
  Players: { [key: string]: PlayerConfig };
  CustomGameSettings: CustomGameSettings;
  HudSettings: HudSettings;
  bIsCasualSpecial: boolean;
  bAllowMapHazards: boolean;
  RiftNodeAttunement: string;
  CountdownDisplay: string;
  Cluster: string;
  WorldBuffs: any[];
  bIsTutorial: boolean;
  MatchId: string;
  bIsOnlineMatch: boolean;
  ModeString: string;
  Map: string;
  bIsRift: boolean;
}

interface CustomGameSettings {
  bHazardsEnabled: boolean;
  bShieldsEnabled: boolean;
  MatchTime: number;
  NumRingouts: number;
}

interface HudSettings {
  bDisplayPortraits: boolean;
  bDisplayStocks: boolean;
  bDisplayTimer: boolean;
}

interface Payload {
  match: {
    id: string;
  };
  custom_notification: string;
}

interface GameNotification {
  data: MatchData;
  payload: Payload;
  header: string;
  cmd: string;
}

export interface PlayerConfig {
  Taunts: string[];
  BotBehaviorOverride: string;
  AccountId: string;
  bAutoPartyPreference: boolean;
  Gems: any[]; // Assuming Gems is an array of any type, replace with specific type if known
  PartyMember: any | null; // Replace `any` with the specific type if known
  GameplayPreferences: number;
  BotDifficultyMax: number;
  bIsBot: boolean;
  RankedDivision: any | null; // Replace `any` with the specific type if known
  bUseCharacterDisplayName: boolean;
  StartingDamage: number;
  TeamIndex: number;
  ProfileIcon: string;
  WinStreak: any | null; // Replace `any` with the specific type if known
  RankedTier: any | null; // Replace `any` with the specific type if known
  Handicap: number;
  RingoutVfx: string;
  Character: string;
  Banner: string;
  StatTrackers: [string, number][];
  Perks: string[];
  PlayerIndex: number;
  PartyId: string;
  Username: Record<string, any>; // Assuming Username is an object, replace with specific type if known
  Buffs: any[]; // Replace `any` with the specific type if known
  Skin: string;
  BotDifficultyMin: number;
}

const PING_BUFFER = Buffer.from([0x0c]);

export class WebSocketService {
  private ws: WebSocketServer;
  clients: Map<string, WebSocketPlayer> = new Map();
  pendingRejoin: Set<string> = new Set(); // Players whose WS will be force-closed for rejoin — skip cleanup
  redisSub: RedisClient;

  constructor(server: Server | HttpServer) {
    this.redisSub = initRedisSubscriber();
    this.ws = new WebSocketServer({ server });
    this.setupSocketHandlers();
    this.setupRedisSubscription();
    this.handleHeartBeats();
  }

  handleHandshake(playerWS: WebSocketPlayer, message: Buffer) {
    const decodedBody = parseInitHydraWebsocketMessage(message);
    // Send ID, hard coded for now
    let buffer = Buffer.from([
      0x09, 0x01, 0x00, 0x24, 0x39, 0x35, 0x34, 0x65, 0x37, 0x37, 0x36, 0x30, 0x2d, 0x35, 0x33, 0x39, 0x62, 0x2d, 0x34, 0x33, 0x36, 0x63, 0x2d, 0x61,
      0x35, 0x37, 0x64, 0x2d, 0x62, 0x35, 0x36, 0x32, 0x33, 0x66, 0x36, 0x37, 0x61, 0x37, 0x34, 0x64,
    ]);
    playerWS.init = true;
    playerWS.account = decodedBody.account;
    playerWS.sendRaw(buffer);

    // DIAGNOSTIC: Detect when a new WebSocket replaces an existing one (zombie detection)
    const existingClient = this.clients.get(playerWS.account.id);
    if (existingClient) {
      logger.warn(
        `[${serviceName}]: ZOMBIE-DIAG: Player ${playerWS.account.id} connecting with NEW WebSocket — replacing existing connection. ` +
        `Old WS readyState=${existingClient.ws.readyState}, deleted=${existingClient.deleted}`,
      );
    }

    this.clients.set(playerWS.account.id, playerWS);

    // Clear pending rejoin flag if this is a reconnect after force-close
    if (this.pendingRejoin.has(playerWS.account.id)) {
      logger.info(`[${serviceName}]: Player ${playerWS.account.id} RECONNECTED after lobby rejoin force-close`);
      this.pendingRejoin.delete(playerWS.account.id);
    }

    redisAddOnlinePlayer(playerWS.account.id).catch((err) => logger.error(`${logPrefix} Error adding online player: ${err}`));
    logger.info(
      `[${serviceName}]: Player ${playerWS.account.id} with IP ${playerWS.ip} and name ${playerWS.account.username} connected to websocket`,
    );
  }

  handleHeartBeats() {
    setInterval(() => {
      for (const [
        _,
        playerWS,
      ] of this.clients) {
        playerWS.sendRaw(PING_BUFFER);
      }
    }, 20000);
  }

  async handleDisconnect(playerWS: WebSocketPlayer) {
    if (playerWS && playerWS.account) {
      const playerId = playerWS.account.id;

      // If this connection is no longer the active one for this player, it's a zombie/stale
      // connection (e.g. replaced by a reconnect). Ignore it entirely — don't clean up
      // Redis data or disband the party, since the player is still actively connected.
      const currentClient = this.clients.get(playerId);
      if (currentClient && currentClient !== playerWS) {
        logger.info(
          `[${serviceName}]: Ignoring stale WebSocket close for player ${playerId} — newer connection is active`,
        );
        return;
      }

      // If this player is pending rejoin (force-closed for lobby rejoin),
      // skip all cleanup so their lobby data stays intact for when they reconnect
      if (this.pendingRejoin.has(playerId)) {
        logger.info(
          `[${serviceName}]: Player ${playerId} disconnected for REJOIN — skipping cleanup, waiting for reconnect`,
        );
        playerWS.deleted = true;
        this.clients.delete(playerId);
        // Don't remove from online_players, don't clean up lobby, don't delete keys
        // The game will reconnect and go through the full init flow
        return;
      }

      // Check if this player was in a multi-player lobby — if so, disband the party
      // so remaining players aren't stuck with a ghost teammate.
      // Uses DLL notifications instead of WS force-close (no 4s main menu flash).
      try {
        const lobbyId = await redisGetPlayerLobby(playerId);
        if (lobbyId) {
          const lobbyState = await redisGetLobbyState(lobbyId);
          if (lobbyState && lobbyState.playerIds.length > 1) {
            const otherPlayerIds = lobbyState.playerIds.filter(pid => pid !== playerId);
            logger.info(
              `[${serviceName}]: Player ${playerId} disconnected from multi-player lobby ${lobbyId} — disbanding party. Remaining: [${otherPlayerIds.join(", ")}]`,
            );

            const isDisconnectedPlayerOwner = playerId === lobbyState.ownerId;

            // Send PlayerLeftLobby WS notification to remaining players
            const leftMsg = {
              targetPlayerIds: otherPlayerIds,
              excludePlayerIds: [],
              notification: {
                data: {
                  MatchID: lobbyId,
                  template_id: "PlayerLeftLobby",
                  Player: {
                    Account: { id: playerId },
                    LobbyPlayerIndex: 0,
                    JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                    BotSettingSlug: "",
                    CrossplayPreference: 1,
                  },
                  ReadyPlayers: {},
                  NewLeader: otherPlayerIds[0],
                },
                payload: { custom_notification: "realtime", match: { id: lobbyId } },
                cmd: "update",
                header: "",
              },
            };
            await redisClient.publish("custom_lobby:notification", JSON.stringify(leftMsg));
            logger.info(`[${serviceName}]: Sent PlayerLeftLobby to remaining players after ${playerId} disconnected`);

            // Also clear ready states
            await redisClient.del(`party_ready:${lobbyId}`);

            if (isDisconnectedPlayerOwner) {
              // Owner disconnected — delete the shared lobby, give remaining players new solo lobbies
              await redisDeleteLobbyState(lobbyId);
              await redisDeletePlayerLobby(playerId);

              for (const pid of otherPlayerIds) {
                try {
                  const rConn = (await redisClient.hGetAll(`connections:${pid}`)) as unknown as RedisPlayerConnection;
                  const newLobby = await createLobby(pid, LOBBY_MODES.ONE_V_ONE);
                  const newLobbyState: RedisLobbyState = {
                    lobbyId: newLobby.id,
                    ownerId: pid,
                    ownerUsername: rConn?.username || rConn?.hydraUsername || "Unknown",
                    mode: LOBBY_MODES.ONE_V_ONE.toString(),
                    playerIds: [pid],
                    createdAt: Date.now(),
                  };
                  await redisSaveLobbyState(newLobby.id, newLobbyState);
                  await redisSavePlayerLobby(pid, newLobby.id);

                  const partyKey = await redisClient.hGet(`connections:${pid}`, "party_key");
                  if (partyKey) await redisUpdatePartyKeyLobby(partyKey, newLobby.id);

                  await redisSetPendingJoinLobby(pid, newLobby.id);
                  logger.info(`[${serviceName}]: Created solo lobby ${newLobby.id} for remaining player ${pid}`);
                } catch (innerErr) {
                  logger.error(`[${serviceName}]: Error creating solo lobby for ${pid}: ${innerErr}`);
                }
              }
            } else {
              // Non-owner disconnected — owner keeps their lobby, just remove the disconnected player
              lobbyState.playerIds = [lobbyState.ownerId];
              lobbyState.mode = LOBBY_MODES.ONE_V_ONE.toString();
              await redisSaveLobbyState(lobbyId, lobbyState);
              await redisDeletePlayerLobby(playerId);

              await redisSetPendingJoinLobby(lobbyState.ownerId, lobbyId);
              logger.info(`[${serviceName}]: Owner ${lobbyState.ownerId} keeps lobby ${lobbyId}, removed disconnected player ${playerId}`);
            }
          }
        }
      } catch (err) {
        logger.error(`[${serviceName}]: Error disbanding party for disconnected player ${playerId}: ${err}`);
      }

      // Handle player disconnect during a match.
      // - PREGAME: process dodge ELO immediately + send match_cancel DLL (match never plays out)
      // - MID-GAME: flag ranked_disconnect and let the remaining player finish. When they hit
      //   READY, the existing concede-on-disconnect logic in handleRankedSetCheckin fires.
      // - POST-GAME (game_result_received): normal disconnect, skip everything.
      try {
        const matchId = playerWS.matchConfig?.data?.GameplayConfig?.MatchId;
        if (matchId) {
          const gameResultReceived = await redisClient.get(`game_result_received:${matchId}`);
          const matchStarted = await redisClient.get(`match_started:${matchId}`);

          if (gameResultReceived) {
            logger.info(`[${serviceName}]: Skipping WS dodge for ${playerId} — game ${matchId} already has a result (normal post-game disconnect)`);
          } else if (matchStarted) {
            // Mid-game: set disconnect flag so auto-concede fires when remaining player hits READY
            logger.info(`[${serviceName}]: Player ${playerId} disconnected mid-match ${matchId} — flagging for auto-concede (remaining player finishes game)`);
            await redisClient.set(`ranked_disconnect:${playerId}`, "1", { EX: 600 });
          } else {
            // Pregame dodge: match hasn't started, process ELO immediately
            const matchConfig = await redisGetMatchConfig(matchId);
            if (matchConfig && !matchConfig.isCustomGame) {
              const leaver = matchConfig.players.find((p) => p.playerId === playerId);
              if (leaver) {
                const winnerTeam = leaver.teamIndex === 0 ? 1 : 0;
                const team0Ids = matchConfig.players.filter((p) => p.teamIndex === 0).map((p) => p.playerId);
                const team1Ids = matchConfig.players.filter((p) => p.teamIndex === 1).map((p) => p.playerId);
                const winnerIds = winnerTeam === 0 ? team0Ids : team1Ids;
                const loserIds = winnerTeam === 0 ? team1Ids : team0Ids;

                const setId = await redisClient.get(`player_ranked_set:${playerId}`) || matchId;
                const canProcess = await redisClient.set(`elo_processed_set:${setId}`, "pregame_dodge", { NX: true, EX: 300 });
                if (canProcess === "OK") {
                  await redisClient.set(`elo_processed:${matchId}`, "1", { NX: true, EX: 300 });
                  try {
                    const chars = new Map<string, string>();
                    for (const pid of [...winnerIds, ...loserIds]) {
                      try {
                        const conn = await redisClient.hGetAll(`connections:${pid}`);
                        if (conn?.character) chars.set(pid, conn.character);
                      } catch {}
                    }
                    await processSetResult(winnerIds, loserIds, matchConfig.mode, [0, 0] as [number, number], winnerTeam, true, chars, matchId);
                    await redisClient.publish("ranked_set:fullrankupdate", JSON.stringify({ playerIds: matchConfig.players.map((p) => p.playerId) }));
                    logger.info(`[${serviceName}]: Pregame dodge: ${playerId} (${playerWS.account.username}) left match ${matchId} — ELO processed`);
                  } catch (eloErr) {
                    logger.error(`[${serviceName}]: Error processing pregame dodge ELO: ${eloErr}`);
                  }

                  // Clean up set + send match_cancel DLL to remaining players
                  const allSetPlayerIds = matchConfig.players.map((p) => p.playerId);
                  for (const pid of allSetPlayerIds) {
                    await redisClient.del(`player_ranked_set:${pid}`);
                  }
                  if (setId !== matchId) {
                    await redisClient.del(`ranked_set:${setId}`);
                    await redisClient.del(`ranked_set_checkins:${setId}`);
                  }

                  for (const pid of allSetPlayerIds) {
                    if (pid === playerId) continue;
                    const remainClient = this.clients.get(pid);
                    if (remainClient) remainClient.matchConfig = undefined;
                    try {
                      await redisPushDLLNotification(pid, {
                        type: "match_cancel",
                        title: "Match Cancelled",
                        message: "Opponent left the match",
                        data: { matchId, reason: "opponent_dodge" },
                        timestamp: Date.now(),
                      });
                      logger.info(`[${serviceName}]: Pushed match_cancel DLL notification to ${pid}`);
                    } catch (notifErr) {
                      logger.error(`[${serviceName}]: Error pushing match_cancel notif to ${pid}: ${notifErr}`);
                    }
                  }
                }
              }
            }
          }
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error processing match leave on disconnect for ${playerId}: ${e}`);
      }

      // If the player is in an active ranked set, flag for auto-concede
      // (player_ranked_set cleanup happens on next login in generateStaticAccess)
      try {
        const setId = await redisClient.get(`player_ranked_set:${playerId}`);
        if (setId) {
          logger.info(`[${serviceName}]: Player ${playerId} disconnected during ranked set ${setId} — flagging for auto-concede`);
          await redisClient.set(`ranked_disconnect:${playerId}`, setId, { EX: 120 });
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error flagging ranked disconnect for ${playerId}: ${e}`);
      }

      // Remove from custom lobby (web-based) if they're in one
      try {
        await leaveLobby(playerId);
      } catch (e) {
        // ignore — they may not be in a custom lobby
      }

      // Remove from SSC custom lobby if they're in one
      try {
        const lobbyKeys = await redisClient.keys("custom_lobby_ssc:*");
        for (const key of lobbyKeys) {
          const raw = await redisClient.get(key);
          if (raw && raw.includes(playerId)) {
            const lobbyId = key.replace("custom_lobby_ssc:", "");
            logger.info(`[${serviceName}]: Removing disconnected player ${playerId} from SSC custom lobby ${lobbyId}`);
            await leaveSscCustomLobby(lobbyId, playerId, false);
            break;
          }
        }
      } catch (e) {
        // ignore — they may not be in a custom lobby
      }

      // Remove from party lobby if they're in one — notify remaining player
      try {
        const partyLobbyId = await redisClient.get(`player_lobby:${playerId}`);
        if (partyLobbyId) {
          const partyRaw = await redisClient.get(`lobby:${partyLobbyId}`);
          if (partyRaw) {
            const partyState = JSON.parse(partyRaw);
            if (partyState.playerIds && partyState.playerIds.includes(playerId) && partyState.playerIds.length > 1) {
              partyState.playerIds = partyState.playerIds.filter((pid: string) => pid !== playerId);
              await redisClient.set(`lobby:${partyLobbyId}`, JSON.stringify(partyState), { EX: 3600 });
              await redisClient.del(`party_ready:${partyLobbyId}`);

              // Notify remaining players via PlayerLeftLobby
              const msg = {
                targetPlayerIds: partyState.playerIds,
                excludePlayerIds: [],
                notification: {
                  data: {
                    MatchID: partyLobbyId,
                    template_id: "PlayerLeftLobby",
                    Player: {
                      Account: { id: playerId },
                      LobbyPlayerIndex: 0,
                      JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                      BotSettingSlug: "",
                      CrossplayPreference: 1,
                    },
                    ReadyPlayers: {},
                    NewLeader: partyState.playerIds[0],
                  },
                  payload: { custom_notification: "realtime", match: { id: partyLobbyId } },
                  cmd: "update",
                  header: "",
                },
              };
              await redisClient.publish("custom_lobby:notification", JSON.stringify(msg));
              logger.info(`[${serviceName}]: Sent PlayerLeftLobby to remaining players in party lobby ${partyLobbyId} after ${playerId} disconnected`);
            }
          }
        }
      } catch (e) {
        // ignore
      }

      playerWS.deleted = true;
      await this.stopMatchTick(playerWS);
      await this.attemptRemoveMatchTicket(playerWS);
      this.clients.delete(playerId);
      await redisRemoveOnlinePlayer(playerId);
      await redisCleanupPlayerLobby(playerId); // Clean up lobby state before deleting player keys
      await redisDeletePlayerKeys(playerId);
      await redisDeleteConnectionKeysByIp(playerWS.ip);
      logger.info(
        `[${serviceName}]: Player ${playerId} with IP ${playerWS.ip} and name ${playerWS.account.username} disconnected from websocket`,
      );
    }
  }

  async attemptRemoveMatchTicket(playerWS: WebSocketPlayer) {
    if (playerWS.ticket) {
      await redisPopMatchTicketsFromQueue("1v1", [playerWS.ticket]);
      await redisPopMatchTicketsFromQueue("2v2", [playerWS.ticket]);
    }
  }

  setupSocketHandlers() {
    this.ws.on("connection", (ws, request) => {
      let ip = request.socket.remoteAddress!.replace(/^::ffff:/, "");
      logger.info(`${logPrefix} Client with IP ${ip} connected`);

      const playerWS = new WebSocketPlayer(ws, ip!);
      ws.on("message", (message) => {
        if (!playerWS.init) {
          if (Buffer.isBuffer(message)) {
            this.handleHandshake(playerWS, message);
            // Need to send ping to client or client will disconnect
            playerWS.sendRaw(PING_BUFFER);
          }
        }
      });

      ws.on("close", async () => {
        await this.handleDisconnect(playerWS);
      });

      ws.on("error", (error) => {
        logger.error(
          `[${serviceName}]: WebSocket error for player ${playerWS.account?.id ?? "unknown"} with IP ${playerWS.ip} and name ${playerWS.account?.username ?? "unknown"}, error: ${JSON.stringify(error)}`,
        );
      });
    });
  }

  async stopMatchTick(player: WebSocketPlayer) {
    // Clear the 100s auto-cancel timeout — match was found, no need to auto-cancel
    if (player.matchTimeout) {
      clearTimeout(player.matchTimeout);
      player.matchTimeout = undefined;
    }

    if (player.matchTick) {
      logger.info(
        `[${serviceName}]: Stopping matchtick for player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"}`,
      );
      clearInterval(player.matchTick);
      player.matchTick = undefined;
      // Match was found — mark as in_match so party join is still blocked
      if (player.account?.id) {
        await redisUpdatePlayerStatus(player.account.id, "in_match");
      }
    }
  }

  async handlePartyQueued(notification: ON_MATCH_MAKER_STARTED_NOTIFICATION) {
    // Check for duplicate tickets BEFORE starting the tick — prevents ghost queues
    const existingTickets = await redisGetMatchTickets(notification.matchType);
    const newPlayerIds = new Set(notification.players.map((p) => p.id));
    const hasDuplicate = existingTickets.some((t) =>
      t.players.some((p) => newPlayerIds.has(p.id)),
    );
    if (hasDuplicate) {
      logger.warn(
        `${logPrefix} Duplicate ticket blocked — player(s) ${Array.from(newPlayerIds).join(", ")} already in ${notification.matchType} queue. Removing stale tickets and re-queuing.`,
      );
      // Remove the stale tickets for these players so the new one can be pushed
      const staleTickets = existingTickets.filter((t) =>
        t.players.some((p) => newPlayerIds.has(p.id)),
      );
      await redisPopMatchTicketsFromQueue(notification.matchType, staleTickets);
    }

    for (const player of notification.players) {
      const client = this.clients.get(player.id);
      if (client) {
        await redisUpdatePlayerStatus(player.id, "queued");
        client.send({
          data: {
            template_id: "OnMatchmakerStarted",
            MatchmakingRequestId: notification.matchmakingRequestId,
          },
          payload: {
            match: {
              id: notification.partyId,
            },
            custom_notification: "realtime",
          },
          header: "",
          cmd: "update",
        });
        client.ticket = notification;
        this.handleMatchTick(client, notification);
      }
    }
    // Add ticket to the matchmaking queue
    await redisPushTicketToQueue(notification.matchType, notification);
  }

  handleMatchTick(client: WebSocketPlayer, notification: ON_MATCH_MAKER_STARTED_NOTIFICATION) {
    // Clear any existing zombie timeout from a previous queue cycle
    if (client.matchTimeout) {
      clearTimeout(client.matchTimeout);
    }

    client.matchTick = setInterval(() => {
      client.send({
        data: {},
        payload: {
          id: notification.matchmakingRequestId,
          state: 2,
        },
        header: "matchmaking-tick",
        cmd: "matchmaking-tick",
      });
    }, 1000);

    client.matchTimeout = setTimeout(async () => {
      await this.cancelMatchMaking(client, notification.matchmakingRequestId);
    }, 100_000);
  }

  async cancelMatchMaking(client: WebSocketPlayer, matchmakingRequestId: string) {
    // Always clear the 100s timeout to prevent zombie timeouts from canceling future queues
    if (client.matchTimeout) {
      clearTimeout(client.matchTimeout);
      client.matchTimeout = undefined;
    }

    if (client.matchTick) {
      const message = {
        data: {},
        payload: {
          id: matchmakingRequestId,
          state: 3,
        },
        header: "Matchmaking request cancelled.",
        cmd: "matchmaking-cancel",
      };
      await this.attemptRemoveMatchTicket(client);
      clearInterval(client.matchTick);
      client.matchTick = undefined;
      client.send(message);
      // Clear the "queued" status so the player isn't stuck as searching
      if (client.account?.id) {
        await redisUpdatePlayerStatus(client.account.id, "idle");
      }
      logger.trace(
        `[${serviceName}]: Canceling matchmaking - ${client.account?.id ?? "unknown"} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} - matchmakingRequestId: ${matchmakingRequestId}`,
      );
    }
  }

  async handleMatchFound(notification: MATCH_FOUND_NOTIFICATION) {
    const arr = [
      env.UDP_SERVER_IP,
      env.UDP_SERVER_IP2,
    ];
    const randomIndex = Math.floor(Math.random() * arr.length);
    for (const matchPlayer of notification.players) {
      let tempPlayer = null;
      try {
        tempPlayer = this.clients.get(matchPlayer.playerId);
      }
      catch (error) {
        logger.error(
          `[${serviceName}]: Error getting player from clients map for player ${matchPlayer.playerId} with IP ${matchPlayer.ip}, error: ${JSON.stringify(error)}`,
        );
      }

      if (!tempPlayer) {
        logger.error(
          `[${serviceName}]: Could not find player ${matchPlayer.playerId} with IP ${matchPlayer.ip} to prepare match found notification for match ${notification.matchId}`,
        );
        continue;
      }

      const player = tempPlayer;
      if (player) {
        try {
          await this.stopMatchTick(player);
        }
        catch (error) {
          logger.error(
            `[${serviceName}]: Error stopping match tick for player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"}`,
            error,
          );
        }

        //logger.info(player);

        //const gameServerPort: Promise<number | undefined> = redisGetGamePort(notification.matchId).then(port => port) || GAME_SERVER_PORT;
        //const gameServerPort = redisGetGamePort(notification.matchId).then(port => port);
        const gameServerPort = notification.rollbackPort || GAME_SERVER_PORT;
        logger.info(
          `[${serviceName}]: Match ${notification.matchId} found for player ${player.account?.id ?? "unknown"}, sending match found notification with game server port ${gameServerPort}`,
        );
        const message = {
          data: {
            MatchKey: notification.matchKey,
            MatchID: notification.matchId,
            //Port: gameServerPort || GAME_SERVER_PORT,
            Port: gameServerPort,
            template_id: "GameServerReadyNotification",
            IPAddress: player.ip === "127.0.0.1" ? "127.0.0.1" : arr[randomIndex],
          },
          payload: {
            match: {
              id: notification.matchId,
            },
            custom_notification: "realtime",
          },
          header: "",
          cmd: "update",
        };

        //logger.info(player.account?.id, message);
        try {
          player.send(message);
        }
        catch (error) {
          logger.error(
            `[${serviceName}]: Error sending match found notification to player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"} for match ${notification.matchId}, error: ${JSON.stringify(error)}`,
          );
          continue;
        }

        logger.info(`${logPrefix} Sent match notification to player ${matchPlayer.playerId} for match ${notification.matchId}`);
      }
    }
    try {
      this.handleSendGamePlayConfig(notification);
    }
    catch (error) {
      logger.error(`${logPrefix} Error handling send gameplay config for match ${notification.matchId}, error: ${JSON.stringify(error)}`);
    }
  }

  getNumRingouts(notification: MATCH_FOUND_NOTIFICATION) {
    if (notification.mode == "1v1") {
      return 3;
    }
    if (notification.mode == "2v2") {
      return 4;
    }
    return 3;
  }

  async handleSendGamePlayConfig(notification: MATCH_FOUND_NOTIFICATION) {
    const MatchTime = notification.customMatchTime ?? 420;
    const NumRingouts = notification.customNumRingouts ?? this.getNumRingouts(notification);

    // Get the player configs from redis
    const playerIds = notification.players.map((p) => p.playerId);
    //const playerConfigs = await redisGetAllPlayersEquippedCosmetics(playerIds);
    let playerConfigs: Cosmetics[] = [];
    for (const playerId of playerIds) {
      // //const cosmetics = await getEquippedCosmetics(playerId);
      // const cosmetics = await redisClient.hGetAll(`connections:${playerId}:cosmetics`) as unknown as Cosmetics;

      // if (!cosmetics || !cosmetics.Banner) {
      //   logger.warn(`No cosmetics found in Redis for player ID ${playerId} during gameplay config preparation. This should not happen, as cosmetics should be cached when the player equips a cosmetic. Creating default cosmetics for this account.`);
      //   const dbCosmetics = await getEquippedCosmetics(playerId);
      //   await redisSetPlayerConnectionCosmetics(playerId, dbCosmetics);
      //   playerConfigs.push(dbCosmetics);
      //   // let rPlayerCosmetics = await getEquippedCosmetics(playerId) as Cosmetics;
      //   // await redisSetPlayerConnectionCosmetics(playerId, rPlayerCosmetics);
      //   // playerConfigs.push(rPlayerCosmetics);
      //   // continue;
      // }
      // else
      // {
      //   playerConfigs.push(cosmetics);
      // }

      const rawCosmetics = await redisClient.hGetAll(`connections:${playerId}:cosmetics`);
      const matchPlayer = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;

      if (!rawCosmetics || Object.keys(rawCosmetics).length === 0) {
        logger.warn(
          `[${serviceName}]: No cosmetics found for player ${playerId} with IP ${matchPlayer.current_ip} and name ${matchPlayer.username ?? "unknown"} in Redis, fetching from database`,
        );
        const dbCosmetics = await getEquippedCosmetics(playerId);
        await redisSetPlayerConnectionCosmetics(playerId, dbCosmetics);
        playerConfigs.push(dbCosmetics);
      }
      else {
        // Parse the JSON strings back into objects
        const cosmetics: Cosmetics = {} as Cosmetics;
        for (const [
          key,
          value,
        ] of Object.entries(rawCosmetics)) {
          try {
            cosmetics[key as keyof Cosmetics] = JSON.parse(value as string);
          }
          catch (e) {
            logger.error(
              `[${serviceName}]: Failed to parse cosmetic field ${key} for player ${playerId} with IP ${matchPlayer.current_ip} and name ${matchPlayer.username ?? "unknown"}, error: ${JSON.stringify(e)}. This should not happen, as all cosmetics should be stored as JSON strings in Redis. Assigning default value for this field.`,
            );
            cosmetics[key as keyof Cosmetics] = value as any;
          }
        }
        playerConfigs.push(cosmetics);
      }
    }
    const playerLoadouts = await redisGetPlayers(playerIds);

    // Get the player configs from redis
    // Separate spectators from active players
    const gamePlayers = notification.players.filter((p) => !p.isSpectator);
    const spectatorPlayers = notification.players.filter((p) => p.isSpectator);

    const Players: { [key: string]: PlayerConfig } = {};
    const Spectators: { [key: string]: PlayerConfig } = {};

    for (let i = 0; i < notification.players.length; i++) {
      const player = notification.players[i];
      //const rPlayerByConnectionId = (await redisClient.hGetAll(`connections:${player.playerId}`)) as unknown as RedisPlayerConnection;
      const rPlayerConnectionByID = await redisClient.hGetAll(`connections:${player.playerId}`) as unknown as RedisPlayerConnection;
      const playerLoadout = playerLoadouts[i];
      const playerConfig = playerConfigs[i];
      const profileIcon = rPlayerConnectionByID.profileIcon ?? "profile_icon_default_gold";
      const character = rPlayerConnectionByID.character ?? "character_jason";
      const skin = rPlayerConnectionByID.skin ?? "skin_jason_000";
      const GameplayPreferences: number = Number(rPlayerConnectionByID.GameplayPreferences) || 964;

      logger.info(
        `[${serviceName}]: Building config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}, GameplayPreferences: ${GameplayPreferences}`,
      );
      //const GameplayPreferences = playerMongoObject?.GameplayPreferences || 964;

      try {
        // // Parse Taunts if it's a string (from Redis)
        // let taunts = playerConfig.Taunts;
        // if (typeof taunts === 'string') {
        //   try {
        //     taunts = JSON.parse(taunts);
        //   } catch (e) {
        //     logger.error(`${logPrefix} Failed to parse Taunts for player ${player.playerId}: ${e}`);
        //     taunts = {};
        //   }
        // }

        // // Parse StatTrackers if it's a string (from Redis)
        // let statTrackers = playerConfig.StatTrackers;
        // if (typeof statTrackers === 'string') {
        //   try {
        //     statTrackers = JSON.parse(statTrackers);
        //   } catch (e) {
        //     logger.error(`${logPrefix} Failed to parse StatTrackers for player ${player.playerId}: ${e}`);
        //     statTrackers = { StatTrackerSlots: ["stat_tracking_bundle_default", "stat_tracking_bundle_default", "stat_tracking_bundle_default"] };
        //   }
        // }

        // // Safely get character taunts with fallback
        // const characterTaunts = taunts?.[character]?.TauntSlots || ["", "", "", ""];
        // const statTrackerSlots = statTrackers?.StatTrackerSlots || ["stat_tracking_bundle_default", "stat_tracking_bundle_default", "stat_tracking_bundle_default"];

        const characterTaunts = playerConfig.Taunts?.[character]?.TauntSlots || [
          "",
          "",
          "",
          "",
        ];
        const statTrackerSlots = playerConfig.StatTrackers?.StatTrackerSlots || [
          "stat_tracking_bundle_default",
          "stat_tracking_bundle_default",
          "stat_tracking_bundle_default",
        ];

        // Look up the actual value for each equipped stat tracker
        const ps = await PlayerStatsModel.findOne({ account_id: player.playerId }).lean();
        const psChars1v1 = (ps as any)?.characters_1v1 || {};
        const psChars2v2 = (ps as any)?.characters_2v2 || {};
        // Alias map: AssociatedCharacter (C-code) → stored character slug suffix
        // (for characters where the C-code doesn't match the player-facing slug)
        const ASSOC_CHAR_ALIASES: Record<string, string> = {
          C034: "BananaGuard",
          C015: "taz",
          C017: "creature",       // Iron Giant
          C001: "wonder_woman",
          C003: "superman",
          C016: "tom_and_jerry",  // LeBron / also used for Tom&Jerry, game may vary
        };

        const resolveStatTrackerValue = (slug: string): number => {
          if (!slug || slug === "stat_tracking_bundle_default") return 0;
          let statField = "";
          if (slug.endsWith("highestdamagedealt") || slug.endsWith("_highest_damage_dealt")) statField = "highestDamageDealt";
          else if (slug.endsWith("totaldamagedealt") || slug.endsWith("_total_damage_dealt")) statField = "totalDamageDealt";
          else if (slug.endsWith("ringouts")) statField = "ringouts";
          else if (slug.endsWith("wins") || /wins\d+$/.test(slug)) statField = "wins";
          else return 0;

          const bundle = (INVENTORY_DEFINITIONS as any)?.[slug];
          const assocChar: string = bundle?.data?.AssociatedCharacter || "";

          // Build candidate stored slugs to match against
          const candidates = new Set<string>();
          if (assocChar) {
            candidates.add(`character_${assocChar}`);                  // e.g., character_C030, character_Jake
            candidates.add(`character_${assocChar.toLowerCase()}`);    // e.g., character_c019
            if (ASSOC_CHAR_ALIASES[assocChar]) {
              candidates.add(`character_${ASSOC_CHAR_ALIASES[assocChar]}`); // C034 → character_BananaGuard
            }
          }

          const allChars = { ...psChars1v1, ...psChars2v2 };
          let total = 0;
          let hasMatch = false;
          for (const [charSlug, charData] of Object.entries(allChars) as [string, any][]) {
            // Check direct match or case-insensitive match
            const storedNorm = charSlug.toLowerCase();
            let match = false;
            for (const cand of candidates) {
              if (charSlug === cand || storedNorm === cand.toLowerCase()) { match = true; break; }
            }
            // Also try name-portion match (no underscores, lowercase)
            if (!match) {
              const storedName = charSlug.replace(/^character_/, "").toLowerCase().replace(/_/g, "");
              const badgeName = assocChar.toLowerCase();
              if (storedName === badgeName) match = true;
            }
            if (match) {
              hasMatch = true;
              if (statField === "highestDamageDealt") {
                total = Math.max(total, Number(charData[statField]) || 0);
              } else {
                total += Number(charData[statField]) || 0;
              }
            }
          }
          return Math.round(total);
        };

        // Look up player's ranked tier/division from ELO
        let rankedTier: string | null = null;
        let rankedDivision: number | null = null;
        if (!notification.isCustomGame) {
          try {
            const eloRating = await getOrCreateRating(player.playerId);
            const is2v2 = notification.mode === "2v2";
            const charsField = is2v2 ? "characters_2v2" : "characters_1v1";
            const charSlug = character || "";
            const charData = (eloRating as any)[charsField]?.[charSlug];
            const elo = charData ? charData.elo : (is2v2 ? eloRating.elo_2v2 : eloRating.elo_1v1);
            const tierInfo = eloToTierDivision(elo);
            rankedTier = tierInfo.tier;
            rankedDivision = tierInfo.division;
          } catch (e) {
            logger.warn(`[${serviceName}]: Could not look up rank for ${player.playerId}: ${e}`);
          }
        }

        const configTarget = player.isSpectator ? Spectators : Players;
        configTarget[player.playerId] = {
          Taunts: characterTaunts,
          BotBehaviorOverride: "",
          AccountId: player.playerId,
          bAutoPartyPreference: false,
          Gems: [],
          PartyMember: null,
          //GameplayPreferences: 964,
          GameplayPreferences: GameplayPreferences,
          BotDifficultyMax: 0,
          bIsBot: false,
          RankedDivision: rankedDivision,
          bUseCharacterDisplayName: false,
          StartingDamage: 0,
          TeamIndex: player.teamIndex,
          ProfileIcon: profileIcon,
          WinStreak: null,
          RankedTier: rankedTier,
          Handicap: 0,
          RingoutVfx: playerConfig.RingoutVfx ?? "ring_out_vfx_default",
          Character: character,
          Banner: playerConfig.Banner ?? "banner_default",
          StatTrackers: [
            [statTrackerSlots[0], resolveStatTrackerValue(statTrackerSlots[0])],
            [statTrackerSlots[1], resolveStatTrackerValue(statTrackerSlots[1])],
            [statTrackerSlots[2], resolveStatTrackerValue(statTrackerSlots[2])],
          ],
          Perks: [],
          PlayerIndex: player.playerIndex,
          PartyId: player.partyId,
          // Username: { default: rPlayerConnectionByID.username || "Player" },
          Username: {},
          Buffs: notification.playerBuffs?.[player.playerId] ?? [],
          Skin: skin,
          BotDifficultyMin: 0,
        };

        logger.info(`${logPrefix} Successfully created ${player.isSpectator ? "spectator" : "player"} config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}`);
      }
      catch (error) {
        logger.error(
          `[${serviceName}]: Error creating player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} : ${JSON.stringify(error)}`,
        );
        // logger.error(`${logPrefix} Character: ${character}, PlayerConfig.Taunts: ${JSON.stringify(playerConfig.Taunts)}`);
        // logger.error(`${logPrefix} PlayerConfig.StatTrackers: ${JSON.stringify(playerConfig.StatTrackers)}`);

        // Create a minimal valid config as fallback
        const fallbackTarget = player.isSpectator ? Spectators : Players;
        fallbackTarget[player.playerId] = {
          Taunts: [
            "",
            "",
            "",
            "",
          ],
          BotBehaviorOverride: "",
          AccountId: player.playerId,
          bAutoPartyPreference: false,
          Gems: [],
          PartyMember: null,
          //GameplayPreferences: 964,
          GameplayPreferences: GameplayPreferences,
          BotDifficultyMax: 0,
          bIsBot: false,
          RankedDivision: null,
          bUseCharacterDisplayName: false,
          StartingDamage: 0,
          TeamIndex: player.teamIndex,
          ProfileIcon: "profile_icon_default_gold",
          WinStreak: null,
          RankedTier: null,
          Handicap: 0,
          RingoutVfx: "ring_out_vfx_default",
          Character: character,
          Banner: "banner_default",
          StatTrackers: [
            [
              "stat_tracking_bundle_default",
              1,
            ],
            [
              "stat_tracking_bundle_default",
              1,
            ],
            [
              "stat_tracking_bundle_default",
              1,
            ],
          ],
          Perks: [],
          PlayerIndex: player.playerIndex,
          PartyId: player.partyId,
//          Username: { default: rPlayerConnectionByID.username || "Player" },
          Username: {},
          Buffs: notification.playerBuffs?.[player.playerId] ?? [],
          Skin: skin,
          BotDifficultyMin: 0,
        };
      }

      // try {
      //   logger.info(`Player ${player.playerId} ${player.playerIndex} selected ${playerLoadout.character}`);
      //   Players[player.playerId] = {
      //     AccountId: player.playerId,
      //     Taunts: playerConfig.Taunts[playerLoadout.character].TauntSlots || ["", "", "", ""],
      //     BotBehaviorOverride: "",
      //     bAutoPartyPreference: false,
      //     Gems: [],
      //     PartyMember: null,
      //     GameplayPreferences: 964,
      //     BotDifficultyMax: 0,
      //     bIsBot: false,
      //     RankedDivision: null,
      //     bUseCharacterDisplayName: false,
      //     StartingDamage: 0,
      //     TeamIndex: player.teamIndex,
      //     //ProfileIcon: playerLoadouts[i].profileIcon,
      //     ProfileIcon: profileIcon,
      //     WinStreak: null,
      //     RankedTier: null,
      //     Handicap: 0,
      //     RingoutVfx: playerConfig.RingoutVfx,
      //     //Character: playerLoadout.character,
      //     Character: character,
      //     Banner: playerConfig.Banner,
      //     StatTrackers: playerConfig.StatTrackers.StatTrackerSlots.map((s) => [s, 1]), // TODO: We should get this from database?
      //     Perks: [],
      //     PlayerIndex: player.playerIndex,
      //     PartyId: player.partyId,
      //     Username: {},
      //     Buffs: [],
      //     //Skin: playerLoadout.skin,
      //     Skin: skin,
      //     BotDifficultyMin: 0,
      //   };
      // }

      // catch (error) {
      //   let originalError = error instanceof Error ? error : new Error(String(error));
      //   logger.error(`Error constructing PlayerConfig for player ${player.playerId}, assigning default config. Error: `);
      //   try {
      //     logger.error(originalError);
      //   }
      //   catch (error) {
      //     try {
      //       logger.error(JSON.stringify(originalError));
      //     }
      //     catch (error) {
      //       logger.error("All attempts to print a simple error have failed, because JavaScript fucking sucks.");
      //     }
      //   }
      //   logger.info(`Player ${player.playerId} ${player.playerIndex} selected ${playerLoadout.character}`);
      //   Players[player.playerId] = {
      //     AccountId: player.playerId,
      //     //Taunts: ["", "", "", ""],
      //     Taunts: defaultTaunts[playerLoadout.character]?.TauntSlots || ["", "", "", ""],
      //     BotBehaviorOverride: "",
      //     bAutoPartyPreference: false,
      //     Gems: [],
      //     PartyMember: null,
      //     GameplayPreferences: 964,
      //     BotDifficultyMax: 0,
      //     bIsBot: false,
      //     RankedDivision: null,
      //     bUseCharacterDisplayName: false,
      //     StartingDamage: 0,
      //     TeamIndex: player.teamIndex,
      //     ProfileIcon: playerLoadouts[i].profileIcon || "profile_icon_default_gold",
      //     WinStreak: null,
      //     RankedTier: null,
      //     Handicap: 0,
      //     RingoutVfx: playerConfig.RingoutVfx || "ring_out_vfx_default",
      //     Character: playerLoadout.character,
      //     Banner: playerConfig.Banner || "banner_default",
      //     //StatTrackers: playerConfig.StatTrackers.StatTrackerSlots.map((s) => [s, 1]), // TODO: We should get this from database?
      //     //StatTrackers: [["", 1], ["", 1], ["", 1]],
      //     StatTrackers: [ [ "stat_tracking_bundle_default", 1 ], [ "stat_tracking_bundle_default", 1 ], [ "stat_tracking_bundle_default", 1 ] ],
      //     Perks: [],
      //     PlayerIndex: player.playerIndex,
      //     PartyId: player.partyId,
      //     Username: {},
      //     Buffs: [],
      //     Skin: playerLoadout.skin,
      //     BotDifficultyMin: 0,
      //   };
      // }
      const preparedConfig = player.isSpectator ? Spectators[player.playerId] : Players[player.playerId];
      logger.info(
        `[${serviceName}]: Prepared ${player.isSpectator ? "spectator" : "player"} config for ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}, GameplayPreferences: ${preparedConfig?.GameplayPreferences}`,
      );
    }

    // Create the message to send to the players

    // Look up hazards from map JSON data (PVE_03 always has hazards, others check JSON)
    let stageHazards: boolean = await getMapHazards(notification.map, notification.mode);
    const message: GameNotification = {
      data: {
        MatchId: notification.matchId,
        GameplayConfig: {
          // Reverted — sending an inline object here causes client to fail
          // matchmaking ("matchmaking failed"). Client probably expects null
          // or a string path. Leave null until we know the expected format.
          ArenaModeInfo: null,
          RiftNodeId: "",
          ScoreEvaluationRule: "TargetScoreIsWin",
          bIsPvP: true,
          ScoreAttributionRule: "AttributeToAttacker",
          MatchDurationSeconds: notification.customMatchTime ?? MatchTime,
          Created: {
            _hydra_unix_date: MVSTime(new Date()),
          },
          EventQueueSlug: "",
          bModeGrantsProgress: true,
          TeamData: [],
          Spectators,
          bIsRanked: !(notification.isCustomGame ?? false),
          bIsCustomGame: notification.isCustomGame ?? false,
          Players,
          CustomGameSettings: {
            bHazardsEnabled: notification.customHazards ?? stageHazards,
            bShieldsEnabled: notification.customShields ?? true,
            MatchTime,
            NumRingouts,
          },
          HudSettings: {
            bDisplayPortraits: true,
            bDisplayStocks: true,
            bDisplayTimer: true,
          },
          bIsCasualSpecial: false,
          bAllowMapHazards: stageHazards,
          RiftNodeAttunement: "Attunements:None",
          CountdownDisplay: "CountdownTypes:XvY",
          Cluster: "ec2-us-east-1-dokken",
          WorldBuffs: notification.worldBuffs ?? [],
          bIsTutorial: false,
          MatchId: notification.matchId,
          bIsOnlineMatch: true,
          ModeString: (notification.isCustomGame ?? false) ? notification.mode : `ranked-${notification.mode}`,
          Map: notification.map,
          bIsRift: false,
        },
        template_id: "OnGameplayConfigNotified",
      },
      payload: {
        match: {
          id: notification.matchId,
        },
        custom_notification: "realtime",
      },
      header: "",
      cmd: "update",
    };

    logwrapper.verbose("Message is: ");
    KitchenSink.TryInspectVerbose(message);

    // Store character map in Redis keyed by matchId — survives disconnects/crashes
    // Also used as set-level character cache (characters can't change mid-set)
    const matchCharacters: Record<string, string> = {};
    for (let i = 0; i < notification.players.length; i++) {
      const player = notification.players[i];
      if (!player.isSpectator) {
        const conn = await redisClient.hGetAll(`connections:${player.playerId}`) as any;
        if (conn?.character) matchCharacters[player.playerId] = conn.character;
      }
    }
    await redisClient.set(`match_characters:${notification.matchId}`, JSON.stringify(matchCharacters), { EX: 60 * 20 });

    // Validate config before sending
    const playerCount = Object.keys(message.data.GameplayConfig.Players || {}).length;
    if (playerCount === 0) {
      logger.error(`[${serviceName}]: GameplayConfig has 0 players for match ${notification.matchId} — not sending`);
      return;
    }

    // Send the message to each player in the match
    for (const player of notification.players) {
      const client = this.clients.get(player.playerId);
      if (client) {
        client.send(message);
        client.matchConfig = message;
        logger.info(
          `[${serviceName}]: Sent gameplay config to player ${client.account?.id ?? "unknown"} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.matchId}`,
        );
      }
    }
  }

  async handleAllPerksLocked(notification: RedisAllPerksLockedNotification) {
    // Send the message to each player in the match
    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);
      if (client) {
        if (client.matchConfig) {
          const playerPerk = await redisGetPlayerPerk(notification.containerMatchId, playerId);
          if (playerPerk) {
            client.matchConfig.data.GameplayConfig.Players[playerId].Perks = playerPerk;
            client.matchConfig.data.template_id = "PerksLockedNotification";
          }
          else {
            logger.error(
              `[${serviceName}]: Match Perks are incomplete!!! This should not really happen for player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"}`,
            );
          }
        }
      }
    }
    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);
      if (client && client.matchConfig) {
        client.send(client.matchConfig);
        logwrapper.verbose(JSON.stringify(client.matchConfig.data.GameplayConfig.Players));
        logger.info(
          `[${serviceName}]: Sent all perks lock to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.containerMatchId}`,
        );
      }
    }

    // Also send PerksLockedNotification to spectators — they need it to transition past the countdown
    try {
      const matchConfig = await redisGetMatchConfig(notification.containerMatchId);
      if (matchConfig) {
        const spectatorIds = matchConfig.players.filter((p) => p.isSpectator).map((p) => p.playerId);
        for (const spectatorId of spectatorIds) {
          const client = this.clients.get(spectatorId);
          if (client && client.matchConfig) {
            client.matchConfig.data.template_id = "PerksLockedNotification";
            client.send(client.matchConfig);
            logger.info(
              `[${serviceName}]: Sent all perks lock to spectator ${spectatorId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.containerMatchId}`,
            );
          }
        }
      }
    } catch (e) {
      logger.error(`[${serviceName}]: Error sending perks lock to spectators for match ${notification.containerMatchId}: ${e}`);
    }
  }

  async handleGameServerInstanceReady(notification: RedisGameServerInstanceReadyNotification) {
    let useCentralRollback = env.USE_INTERNAL_ROLLBACK === 1 ? true : false;
    let rollbackHost = useCentralRollback ? `${env.UDP_SERVER_IP}` : "127.0.0.1";
    let playerClients: Record<string, WebSocketPlayer>[] = [];

    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);
      if (!client)
      {
        logger.error(
          `[${serviceName}]: Could not find player ${playerId} to send game server instance ready message for match ${notification.containerMatchId}, canceling matchmaking`,
        );
        cancelMatchmakingForAll(notification.playerIds, notification.containerMatchId);
        return;
      }

      playerClients.push({ [playerId]: client });
    }
    for (const playerId of notification.playerIds) {
      // const client = this.clients.get(playerId);
      const client = playerClients.find(pc => pc[playerId])?.[playerId];

      const gameServerPort = notification.rollbackPort || GAME_SERVER_PORT;
      logger.info(
        `[${serviceName}]: Received game server instance ready for match ${notification.containerMatchId} and player ${playerId} with IP ${client?.ip ?? "unknown"} and name ${client?.account?.username ?? "unknown"}, sending game server info with port ${gameServerPort}`,
      );

      const message = {
        data: {},
        payload: {
          game_server_instance: {
            game_server_type_slug: "multiplay",
            port: gameServerPort || GAME_SERVER_PORT,
            owner_id: notification.containerMatchId,
            //host: "127.0.0.1",
            host: rollbackHost,
            id: notification.resultId,
          },
          proxied_data: null,
        },
        header: "Your game server is ready to join.",
        cmd: "game-server-instance-ready",
      };

      //logger.info(`Spawning rollback process for match ${notification.containerMatchId}`);
      //let rollbackProcess = await RollbackProcess.execRollbackProcessAsync(gameServerPort || GAME_SERVER_PORT);
      // spawn('node', ['bgService.js'], {
      //     stdio: 'ignore', // piping all stdio to /dev/null
      //     detached: true
      // }).unref();
      if (client) {
        logger.info(
          `[${serviceName}]: Sent game server instance ready to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.containerMatchId}`,
        );
        client.send(message);
      }
      else {
        logger.error(
          `[${serviceName}]: Could not find player ${playerId} to send game server instance ready message for match ${notification.containerMatchId}`,
        );
      }
    }
  }

  handleOnLobbyModeChanged(notification: RedisOnLobbyModeUpdatedNotification) {
    for (const playerId of notification.playersIds) {
      const client = this.clients.get(playerId);
      const message = {
        data: {
          template_id: "OnLobbyModeUpdated",
          LobbyId: notification.lobbyId,
          ModeString: notification.modeString,
        },
        payload: {
          custom_notification: "realtime",
        },
        header: "",
        cmd: "update",
      };
      if (client) {
        logger.trace(
          `[${serviceName}]: OnLobbyModeUpdated send to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for lobby ${notification.lobbyId} with mode ${notification.modeString}`,
        );
        client.send(message);
      }
    }
  }

  async handleCancelMatchMaking(notification: RedisCancelMatchMakingNotification) {
    for (const playerId of notification.playersIds) {
      const client = this.clients.get(playerId);

      if (client) {
        await this.cancelMatchMaking(client, notification.matchmakingId);
      }
    }
  }

  handlePartyInvite(notification: RedisPartyInviteNotification) {
    const client = this.clients.get(notification.invitedAccountId);
    if (!client) {
      logger.warn(`[${serviceName}]: Party invite — invitee ${notification.invitedAccountId} not connected to WS`);
      return;
    }
    logger.info(`[${serviceName}]: Sending InviteReceivedForLobby to ${notification.invitedAccountId} from ${notification.inviterAccountId}`);
    client.send({
      data: {
        template_id: "InviteReceivedForLobby",
        LobbyType: 0,
        MatchID: notification.lobbyId,
        ContextData: {
          LobbyType: "Party",
        },
        IsSpectator: false,
        InviterAccountId: notification.inviterAccountId,
      },
      payload: {
        frm: {
          id: "internal-server",
          type: "server-api-key",
        },
        template: "realtime",
        account_id: notification.invitedAccountId,
        profile_id: notification.invitedAccountId,
      },
      header: "",
      cmd: "profile-notification",
    });
  }

  async handleToastReceived(notification: RedisToastNotification) {
    logger.info(`[${serviceName}]: handleToastReceived called — toastee: ${notification?.toasteeAccountId}, toaster: ${notification?.toasterAccountId}`);
    // Queue a DLL notification for the toastee — the DLL's NotificationPoller
    // will pick it up on its next 2s poll and show an in-game banner via
    // ShowBanner. This works because the Hydra WS ToastReceived template_id
    // isn't in the game's dispatch table (see game dump investigation).
    await redisPushDLLNotification(notification.toasteeAccountId, {
      type: "toast_received",
      title: "Toast received!",
      message: `${notification.toasterUsername} toasted you!`,
      data: {},
      timestamp: Date.now(),
    });
    logger.info(`[${serviceName}]: Queued toast_received DLL notification for ${notification.toasteeAccountId} from ${notification.toasterUsername} (${notification.toasterAccountId})`);
  }

  handlePlayerLoadoutLocked(notification: RedisPlayerLoadoutLockedNotification) {
    for (const playerId of notification.targetPlayerIds) {
      const client = this.clients.get(playerId);
      if (client) {
        // If this is a join event, also send PlayerJoinedLobby first
        // to update the game's internal lobby roster (adds the player to Teams)
        if (notification.isJoining) {
          const joinMsg = {
            data: {
              template_id: "PlayerJoinedLobby",
              LobbyId: notification.lobbyId,
              JoinedPlayerId: notification.lockedPlayerId,
            },
            payload: {
              custom_notification: "realtime",
            },
            header: "",
            cmd: "update",
          };
          client.send(joinMsg);
          logger.info(
            `[${serviceName}]: Sent PlayerJoinedLobby to ${playerId} — player ${notification.lockedPlayerId} joined lobby ${notification.lobbyId}`,
          );
        }

        // Send OnPlayerLoadoutLocked — shows the character with golden swirl
        const message = {
          data: {
            template_id: "OnPlayerLoadoutLocked",
            LobbyId: notification.lobbyId,
            AccountId: notification.lockedPlayerId,
            Loadout: {
              Character: notification.character,
              Skin: notification.skin,
            },
            bAreAllLoadoutsLocked: true,
          },
          payload: {
            custom_notification: "realtime",
          },
          header: "",
          cmd: "update",
        };
        client.send(message);
        logger.info(
          `[${serviceName}]: Sent OnPlayerLoadoutLocked to ${playerId} — locked player ${notification.lockedPlayerId} (${notification.character}) in lobby ${notification.lobbyId}`,
        );
      } else {
        logger.warn(`[${serviceName}]: Could not find player ${playerId} to deliver OnPlayerLoadoutLocked`);
      }
    }
  }

  async handlePlayerJoinedLobby(notification: RedisPlayerJoinedLobbyNotification) {
    const allPlayerIds = notification.allPlayerIds;
    const ownerId = notification.ownerId;
    const lobbyId = notification.lobbyId;

    // Build Teams.Players with all players in the lobby
    const teamPlayers: any = {};
    for (let i = 0; i < allPlayerIds.length; i++) {
      teamPlayers[allPlayerIds[i]] = {
        Account: { id: allPlayerIds[i] },
        JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
        BotSettingSlug: "",
        LobbyPlayerIndex: i,
        CrossplayPreference: 1,
      };
    }

    // Build per-player data from Redis — includes identity info for visual UI
    const gameplayPrefs: any = {};
    const autoPartyPrefs: any = {};
    const platforms: any = {};
    const lockedLoadouts: any = {};
    const playerConnections: Map<string, any> = new Map();
    for (const pid of allPlayerIds) {
      const conn = await redisClient.hGetAll(`connections:${pid}`) as any;
      playerConnections.set(pid, conn);
      gameplayPrefs[pid] = Number(conn?.GameplayPreferences) || 964;
      autoPartyPrefs[pid] = false;
      platforms[pid] = "PC";
      lockedLoadouts[pid] = {
        Character: conn?.character || "character_shaggy",
        Skin: conn?.skin || "skin_shaggy_default",
      };
    }

    const lobbyData = {
      Teams: [
        { TeamIndex: 0, Players: teamPlayers, Length: allPlayerIds.length },
        { TeamIndex: 1, Players: {}, Length: 0 },
        { TeamIndex: 2, Players: {}, Length: 0 },
        { TeamIndex: 3, Players: {}, Length: 0 },
        { TeamIndex: 4, Players: {}, Length: 0 },
      ],
      LeaderID: ownerId,
      LobbyType: 0,
      ReadyPlayers: {},
      PlayerGameplayPreferences: gameplayPrefs,
      PlayerAutoPartyPreferences: autoPartyPrefs,
      GameVersion: "local",
      HissCrc: 1167552915,
      Platforms: platforms,
      AllMultiplayParams: {
        "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
        "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
        "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
        "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
      },
      LockedLoadouts: lockedLoadouts,
      ModeString: notification.mode,
      IsLobbyJoinable: true,
      MatchID: lobbyId,
    };

    // Build players identity section — matches PUT /matches/:id response format.
    // The game's visual lobby UI needs player identity data (username, avatar, platform)
    // to render character displays and player names.
    const playersAll: any[] = [];
    for (const pid of allPlayerIds) {
      const conn = playerConnections.get(pid) || {};
      const displayUsername = conn.username || conn.hydraUsername || notification.joinedPlayerUsername || "Unknown";
      const hydraUsername = conn.hydraUsername || displayUsername;
      const wbNetworkId = conn.wb_network_id || pid;
      playersAll.push({
        account_id: pid,
        source: {},
        state: "join",
        data: {},
        identity: {
          username: hydraUsername,
          avatar: "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
          default_username: true,
          personal_data: {},
          alternate: {
            wb_network: [{ id: wbNetworkId, username: displayUsername, avatar: null, email: null }],
            steam: [{ id: "76561195177950873", username: displayUsername, avatar: "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg", email: null }],
          },
          usernames: [
            { auth: "hydra", username: hydraUsername },
            { auth: "steam", username: displayUsername },
            { auth: "wb_network", username: displayUsername },
          ],
          platforms: ["steam"],
          current_platform: "steam",
          is_cross_platform: false,
        },
      });
    }

    const playersSection = {
      all: playersAll,
      current: allPlayerIds,
      count: allPlayerIds.length,
    };

    // Full match response wrapper — same structure as PUT /matches/:id
    const now = Math.floor(Date.now() / 1000);
    const fullMatchResponse = {
      updated_at: { _hydra_unix_date: now },
      created_at: { _hydra_unix_date: now },
      account_id: null,
      completion_time: null,
      name: "white-green-wind-breeze-OS5dF",
      state: "open",
      access_level: "public",
      origin: "client",
      rand: Math.random(),
      winning_team: [],
      win: [],
      loss: [],
      draw: null,
      arbitration: null,
      data: {},
      server_data: lobbyData,
      players: playersSection,
      matchmaking: null,
      cluster: "ec2-us-east-1-dokken",
      last_warning_time: null,
      template: {
        type: "async",
        name: "party_lobby",
        slug: "party_lobby",
        min_players: 2,
        max_players: 2,
        game_server_integration_enabled: false,
        game_server_config: null,
        created_at: { _hydra_unix_date: now },
        updated_at: { _hydra_unix_date: now },
        data: {},
        id: lobbyId,
      },
      criteria: { slug: null },
      shortcode: null,
      id: lobbyId,
      access: "public",
    };

    // Send lobby state to ALL players using multiple message formats.
    // We include players identity data in every message — the game's visual lobby UI
    // needs this to render character displays and player names.
    for (const playerId of allPlayerIds) {
      const client = this.clients.get(playerId);
      if (client) {
        // 1) "lobby-join" — dedicated cmd found in game binary.
        //    Include full match response as the payload so the game has access to
        //    both server_data (lobby state) and players (identity/visual data).
        const lobbyJoinMsg = {
          data: {},
          payload: {
            lobby: lobbyData,
            match: fullMatchResponse,
            party_id: lobbyId,
            players: playersSection,
          },
          header: "",
          cmd: "lobby-join",
        };
        client.send(lobbyJoinMsg);
        logger.info(`[${serviceName}]: Sent lobby-join to ${playerId} for lobby ${lobbyId} (with players identity)`);

        // 2) OnLobbyRuntimeDataUpdated — lobby runtime data update
        const runtimeUpdateMsg = {
          data: {
            template_id: "OnLobbyRuntimeDataUpdated",
            LobbyId: lobbyId,
            ModeString: notification.mode,
            lobby: lobbyData,
            players: playersSection,
          },
          payload: { match: { id: lobbyId }, custom_notification: "realtime" },
          header: "",
          cmd: "update",
        };
        client.send(runtimeUpdateMsg);
        logger.info(`[${serviceName}]: Sent OnLobbyRuntimeDataUpdated to ${playerId} for lobby ${lobbyId}`);

        // 3) PlayerJoinedLobby — player join event
        const playerJoinedMsg = {
          data: {
            template_id: "PlayerJoinedLobby",
            LobbyId: lobbyId,
            ModeString: notification.mode,
            lobby: lobbyData,
            JoinedPlayerId: notification.joinedPlayerId,
            players: playersSection,
          },
          payload: { match: { id: lobbyId }, custom_notification: "realtime" },
          header: "",
          cmd: "update",
        };
        client.send(playerJoinedMsg);
        logger.info(`[${serviceName}]: Sent PlayerJoinedLobby to ${playerId} for lobby ${lobbyId}`);
      }
    }
  }

  // Targeted lobby transition — sends lobby-join + updates to ONE specific player.
  // Used for party invites: the invitee gets pushed into the inviter's lobby
  // without a WS reconnect. The key difference from handlePlayerJoinedLobby is
  // that this targets a single player and hopes the game's "lobby-join" cmd
  // overrides the current lobby context (rather than being filtered by lobby ID).
  async handleLobbyTransition(notification: RedisLobbyTransitionNotification) {
    const { targetPlayerId, lobbyId, ownerId, allPlayerIds, mode, joinedPlayerId, joinedPlayerUsername } = notification;
    // wsLobbyId: the lobby ID to put in WS messages. If provided, use the invitee's
    // OLD lobby ID so the game accepts the messages (it filters by current lobby ID).
    // The actual lobby data (Teams, Players, etc.) is built from the real lobby.
    const msgLobbyId = notification.wsLobbyId || lobbyId;
    const client = this.clients.get(targetPlayerId);
    if (!client) {
      logger.warn(`[${serviceName}]: Cannot send lobby transition to ${targetPlayerId} - not connected`);
      return;
    }

    // Build Teams.Players with all players in the lobby
    const teamPlayers: any = {};
    for (let i = 0; i < allPlayerIds.length; i++) {
      teamPlayers[allPlayerIds[i]] = {
        Account: { id: allPlayerIds[i] },
        JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
        BotSettingSlug: "",
        LobbyPlayerIndex: i,
        CrossplayPreference: 1,
      };
    }

    // Build per-player data from Redis
    const gameplayPrefs: any = {};
    const autoPartyPrefs: any = {};
    const platforms: any = {};
    const lockedLoadouts: any = {};
    const playerConnections: Map<string, any> = new Map();
    for (const pid of allPlayerIds) {
      const conn = await redisClient.hGetAll(`connections:${pid}`) as any;
      playerConnections.set(pid, conn);
      gameplayPrefs[pid] = Number(conn?.GameplayPreferences) || 964;
      autoPartyPrefs[pid] = false;
      platforms[pid] = "PC";
      lockedLoadouts[pid] = {
        Character: conn?.character || "character_shaggy",
        Skin: conn?.skin || "skin_shaggy_default",
      };
    }

    const lobbyData = {
      Teams: [
        { TeamIndex: 0, Players: teamPlayers, Length: allPlayerIds.length },
        { TeamIndex: 1, Players: {}, Length: 0 },
        { TeamIndex: 2, Players: {}, Length: 0 },
        { TeamIndex: 3, Players: {}, Length: 0 },
        { TeamIndex: 4, Players: {}, Length: 0 },
      ],
      LeaderID: ownerId,
      LobbyType: 0,
      ReadyPlayers: {},
      PlayerGameplayPreferences: gameplayPrefs,
      PlayerAutoPartyPreferences: autoPartyPrefs,
      GameVersion: "local",
      HissCrc: 1167552915,
      Platforms: platforms,
      AllMultiplayParams: {
        "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
        "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
        "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
        "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
      },
      LockedLoadouts: lockedLoadouts,
      ModeString: mode,
      IsLobbyJoinable: true,
      MatchID: msgLobbyId,
    };

    // Build players identity section
    const playersAll: any[] = [];
    for (const pid of allPlayerIds) {
      const conn = playerConnections.get(pid) || {};
      const displayUsername = conn.username || conn.hydraUsername || joinedPlayerUsername || "Unknown";
      const hydraUsername = conn.hydraUsername || displayUsername;
      const wbNetworkId = conn.wb_network_id || pid;
      playersAll.push({
        account_id: pid,
        source: {},
        state: "join",
        data: {},
        identity: {
          username: hydraUsername,
          avatar: "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
          default_username: true,
          personal_data: {},
          alternate: {
            wb_network: [{ id: wbNetworkId, username: displayUsername, avatar: null, email: null }],
            steam: [{ id: "76561195177950873", username: displayUsername, avatar: "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg", email: null }],
          },
          usernames: [
            { auth: "hydra", username: hydraUsername },
            { auth: "steam", username: displayUsername },
            { auth: "wb_network", username: displayUsername },
          ],
          platforms: ["steam"],
          current_platform: "steam",
          is_cross_platform: false,
        },
      });
    }

    const playersSection = {
      all: playersAll,
      current: allPlayerIds,
      count: allPlayerIds.length,
    };

    const now = Math.floor(Date.now() / 1000);
    const fullMatchResponse = {
      updated_at: { _hydra_unix_date: now },
      created_at: { _hydra_unix_date: now },
      account_id: null,
      completion_time: null,
      name: "white-green-wind-breeze-OS5dF",
      state: "open",
      access_level: "public",
      origin: "client",
      rand: Math.random(),
      winning_team: [],
      win: [],
      loss: [],
      draw: null,
      arbitration: null,
      data: {},
      server_data: lobbyData,
      players: playersSection,
      matchmaking: null,
      cluster: "ec2-us-east-1-dokken",
      last_warning_time: null,
      template: {
        type: "async",
        name: "party_lobby",
        slug: "party_lobby",
        min_players: 2,
        max_players: 2,
        game_server_integration_enabled: false,
        game_server_config: null,
        created_at: { _hydra_unix_date: now },
        updated_at: { _hydra_unix_date: now },
        data: {},
        id: msgLobbyId,
      },
      criteria: { slug: null },
      shortcode: null,
      id: msgLobbyId,
      access: "public",
    };

    // Send lobby-join using msgLobbyId (invitee's own lobby ID so the game accepts it).
    // The game filters ALL WS messages by current lobby ID, so we must use the
    // invitee's existing lobby ID. The actual player data inside is the real merged roster.
    const lobbyJoinMsg = {
      data: {},
      payload: {
        lobby: lobbyData,
        match: fullMatchResponse,
        party_id: msgLobbyId,
        players: playersSection,
      },
      header: "",
      cmd: "lobby-join",
    };
    client.send(lobbyJoinMsg);
    logger.info(`[${serviceName}]: Sent lobby-join (transition) to ${targetPlayerId} for lobby ${msgLobbyId} (real: ${lobbyId})`);

    // Also send OnLobbyRuntimeDataUpdated and PlayerJoinedLobby
    const runtimeUpdateMsg = {
      data: {
        template_id: "OnLobbyRuntimeDataUpdated",
        LobbyId: msgLobbyId,
        ModeString: mode,
        lobby: lobbyData,
        players: playersSection,
      },
      payload: { match: { id: msgLobbyId }, custom_notification: "realtime" },
      header: "",
      cmd: "update",
    };
    client.send(runtimeUpdateMsg);

    const playerJoinedMsg = {
      data: {
        template_id: "PlayerJoinedLobby",
        LobbyId: msgLobbyId,
        ModeString: mode,
        lobby: lobbyData,
        JoinedPlayerId: joinedPlayerId,
        players: playersSection,
      },
      payload: { match: { id: msgLobbyId }, custom_notification: "realtime" },
      header: "",
      cmd: "update",
    };
    client.send(playerJoinedMsg);
    logger.info(`[${serviceName}]: Sent lobby transition complete to ${targetPlayerId} (3 messages, wsLobbyId=${msgLobbyId}, real=${lobbyId})`);
  }

  handleLobbyRejoin(notification: RedisLobbyRejoinNotification) {
    const { playerId, lobbyId } = notification;
    const client = this.clients.get(playerId);
    if (!client) {
      logger.warn(`[${serviceName}]: Cannot trigger lobby rejoin for ${playerId} - not connected`);
      return;
    }

    logger.info(`[${serviceName}]: Triggering lobby rejoin for ${playerId} — will force-close WebSocket so game reconnects and re-calls create_party_lobby`);

    // Mark this player as "pending rejoin" so the disconnect handler skips cleanup
    this.pendingRejoin.add(playerId);

    // Brief delay for Player 2's response to finish, then force-close Player 1's WebSocket.
    // The game reconnects automatically and goes through the full init flow,
    // including calling create_party_lobby which hits our rejoin path and returns 2-player data.
    setTimeout(() => {
      const currentClient = this.clients.get(playerId);
      if (currentClient && currentClient.ws) {
        logger.info(`[${serviceName}]: Force-closing WebSocket for ${playerId} to trigger rejoin`);
        currentClient.ws.close(1000, "rejoin");
      }

      // Clear the pending rejoin flag after a timeout (in case the game doesn't reconnect)
      setTimeout(async () => {
        if (this.pendingRejoin.has(playerId)) {
          logger.warn(`[${serviceName}]: Player ${playerId} did not reconnect after rejoin — clearing pending flag`);
          this.pendingRejoin.delete(playerId);
          // Clean up since they didn't reconnect
          await redisRemoveOnlinePlayer(playerId);
          await redisCleanupPlayerLobby(playerId);
          await redisDeletePlayerKeys(playerId);
        }
      }, 15000); // 15 seconds timeout for reconnection
    }, 500); // 500ms delay before closing — minimal disruption
  }

  async handleOnMatchEnd(notification: RedisMatchEndNotification) {
    for (const playerId of notification.playersIds) {
      const client = this.clients.get(playerId);

      if (client) {
        if (!client.matchConfig) {
          logger.warn(`${logPrefix} Ignoring END_OF_MATCH for player ${playerId} — no active matchConfig (stale rollback server?)`);
          continue;
        }
        const data = {
          data: {
            GameplayConfig: client.matchConfig.data.GameplayConfig,
            template_id: "EndOfMatchPayload",
            ClientReturnData: {},
          },
          payload: {
            frm: {
              id: "internal-server",
              type: "server-api-key",
            },
            template: "realtime",
            account_id: playerId,
            profile_id: playerId,
          },
          header: "",
          cmd: "profile-notification",
        };
        // Validate critical GameplayConfig fields before sending
        const gc = client.matchConfig.data.GameplayConfig;
        if (!gc?.MatchId || !gc?.Map || !gc?.Players || Object.keys(gc?.Players || {}).length === 0) {
          logger.warn(`${logPrefix} EndOfMatchPayload has missing fields for ${playerId}: MatchId=${gc?.MatchId}, Map=${gc?.Map}, Players=${Object.keys(gc?.Players || {}).length}`);
        }
        logger.info(`${logPrefix} Received End of Match for MatchID: ${gc?.MatchId}`);
        client.send(data);

        // Clear matchConfig and status since the match is over
        client.matchConfig = undefined;
        await redisUpdatePlayerStatus(playerId, "idle");
      }
    }

    // Check if this match belongs to an SSC custom lobby (new system)
    const sscLobbyId = await getSscCustomLobbyForMatch(notification.matchId);
    if (sscLobbyId) {
      logger.info(`[${serviceName}]: Match ${notification.matchId} is an SSC custom lobby match (${sscLobbyId}), handling rematch`);
      await handleSscCustomLobbyMatchEnd(notification.matchId);
      return;
    }

    // Check if this match belongs to a web-based custom lobby (old system)
    const customLobbyCode = await getCustomLobbyForMatch(notification.matchId);
    if (customLobbyCode) {
      // Custom lobby match — DON'T send RematchDeclinedNotification.
      // The lobby service will handle rematch after 25 seconds.
      logger.info(`[${serviceName}]: Match ${notification.matchId} is a custom lobby match (${customLobbyCode}), skipping RematchDeclinedNotification`);
      await handleCustomLobbyMatchEnd(notification.matchId);
    } else {
      // Check if this is a ranked set (best-of-3) — store set state and wait for check-ins
      const matchConfig = await redisGetMatchConfig(notification.matchId);
      let existingSetId = notification.playersIds.length > 0
        ? await redisClient.get(`player_ranked_set:${notification.playersIds[0]}`)
        : null;

      if (existingSetId) {
        logger.info(`[${serviceName}]: Match ${notification.matchId} belongs to set ${existingSetId}`);
      }

      // Check if any player dodged (disconnected mid-match) — set will be marked as conceded
      let dodgedByPlayer = "";
      for (const pid of notification.playersIds) {
        const disconnectFlag = await redisClient.get(`ranked_disconnect:${pid}`);
        if (disconnectFlag) {
          dodgedByPlayer = pid;
          logger.info(`[${serviceName}]: Match ${notification.matchId} — player ${pid} dodged, set will be marked conceded`);
          break;
        }
      }

      if (matchConfig && !matchConfig.isCustomGame) {
        // Verify the existing set is still valid (not already completed/cleaned up)
        let existingSet = existingSetId
          ? JSON.parse(await redisClient.get(`ranked_set:${existingSetId}`) || "null")
          : null;

        // If the set data is gone but the player_ranked_set key still exists, it's stale
        if (existingSetId && !existingSet) {
          logger.info(`[${serviceName}]: Stale player_ranked_set for ${notification.playersIds[0]} (set ${existingSetId} no longer exists), cleaning up`);
          for (const pid of notification.playersIds) {
            await redisClient.del(`player_ranked_set:${pid}`);
          }
          existingSetId = null;
        }

        // Ranked match — check if the set is over or should continue
        const setId = existingSetId || notification.matchId;

        // If ELO was already processed for this set (dodge, concede, or set-over),
        // the set is resolved — don't recreate it. Without this check, a mid-game
        // dodge cleanup followed by a delayed END_OF_MATCH would recreate the set
        // and leave the remaining player stuck in check-in limbo.
        const alreadyProcessed = await redisClient.get(`elo_processed_set:${setId}`);
        if (alreadyProcessed) {
          logger.info(`[${serviceName}]: Set ${setId} already resolved (${alreadyProcessed}), skipping set creation for match ${notification.matchId}`);
          // Send remaining players back to lobby
          for (const pid of notification.playersIds) {
            const remainClient = this.clients.get(pid);
            if (remainClient) {
              remainClient.matchConfig = undefined;
              await redisUpdatePlayerStatus(pid, "idle");
            }
          }
          // Send MatchSetLeaverNotification + empty OnGameplayConfigNotified
          // (same sequence as normal set-complete path so client returns to lobby)
          for (const pid of notification.playersIds) {
            const remainClient = this.clients.get(pid);
            if (!remainClient) continue;
            setTimeout(() => {
              remainClient.send({
                data: {
                  AccountId: pid,
                  MatchId: notification.matchId,
                  template_id: "MatchSetLeaverNotification",
                },
                payload: {
                  match: { id: notification.matchId },
                  template: "realtime",
                  account_id: pid,
                  profile_id: pid,
                  custom_notification: "realtime",
                },
                header: "",
                cmd: "profile-notification",
              });
            }, 1000);
            setTimeout(() => {
              remainClient.send({
                data: { MatchId: "", GameplayConfig: null, template_id: "OnGameplayConfigNotified" },
                payload: { match: { id: "" }, custom_notification: "realtime" },
                header: "",
                cmd: "update",
              });
              logger.info(`[${serviceName}]: Sent empty OnGameplayConfigNotified to ${pid} (set already resolved, return to lobby)`);
            }, 1500);
          }
          return;
        }

        const gamesPlayed = existingSet ? existingSet.gamesPlayed + 1 : 1;
        let scores = existingSet?.scores || [0, 0];

        // For game 1, the set didn't exist when submit_end_of_match_stats fired,
        // so check for a pending score stored directly on the match
        if (!existingSet) {
          const pendingWinner = await redisClient.get(`ranked_set_pending_winner:${notification.matchId}`);
          if (pendingWinner !== null) {
            const winIdx = parseInt(pendingWinner);
            if (winIdx === 0 || winIdx === 1) {
              scores = [0, 0];
              scores[winIdx]++;
            }
            await redisClient.del(`ranked_set_pending_winner:${notification.matchId}`);
          }
        }

        const setOver = scores[0] >= 2 || scores[1] >= 2 || gamesPlayed >= 3;

        if (setOver) {
          // Set is over — process ELO and return to lobby
          logger.info(`[${serviceName}]: Ranked set ${setId} — set complete (${scores[0]}-${scores[1]}) after ${gamesPlayed} games, processing ELO`);

          // Process set-based ELO (with dedup — disconnect concede may have already processed)
          const eloDedup = await redisClient.set(`elo_processed_set:${setId}`, "set_complete", { NX: true, EX: 300 });
          try {
            const team0Ids = existingSet.players.filter((p: any) => p.teamIndex === 0).map((p: any) => p.playerId);
            const team1Ids = existingSet.players.filter((p: any) => p.teamIndex === 1).map((p: any) => p.playerId);
            const winnerTeam = scores[0] > scores[1] ? 0 : 1;
            const winnerIds = winnerTeam === 0 ? team0Ids : team1Ids;
            const loserIds = winnerTeam === 0 ? team1Ids : team0Ids;
            if (eloDedup === "OK") {
            // Get characters: try match_characters (stored at match start), then Redis connections
            const playerChars = new Map<string, string>();
            const matchCharsRaw = await redisClient.get(`match_characters:${notification.matchId}`);
            const matchChars = matchCharsRaw ? JSON.parse(matchCharsRaw) : {};
            for (const pid of [...winnerIds, ...loserIds]) {
              if (matchChars[pid]) {
                playerChars.set(pid, matchChars[pid]);
              } else {
                try {
                  const conn = await redisClient.hGetAll(`connections:${pid}`);
                  if (conn?.character) playerChars.set(pid, conn.character);
                } catch {}
              }
              if (!playerChars.has(pid)) {
                logger.warn(`[${serviceName}]: No character found for player ${pid} — ELO will use global fallback`);
              }
            }
            await processSetResult(winnerIds, loserIds, existingSet.mode, scores as [number, number], winnerTeam, false, playerChars, notification.matchId);
            } else {
              logger.info(`[${serviceName}]: ELO already processed for set ${setId}, skipping set-complete processing`);
            }

            // Send FullRankUpdate notification to each player with fresh ranked data
            for (const pid of [...winnerIds, ...loserIds]) {
              const client = this.clients.get(pid);
              if (!client) continue;
              try {
                const rating = await getOrCreateRating(pid, "");
                const rank1v1 = await getPlayerRank(pid, "1v1");
                const rank2v2 = await getPlayerRank(pid, "2v2");
                const elo1v1 = rating?.elo_1v1 || 0;
                const elo2v2 = rating?.elo_2v2 || 0;
                const wins1v1 = rating?.wins_1v1 || 0;
                const losses1v1 = rating?.losses_1v1 || 0;
                const wins2v2 = rating?.wins_2v2 || 0;
                const losses2v2 = rating?.losses_2v2 || 0;
                const chars1v1 = (rating as any)?.characters_1v1 || {};
                const chars2v2 = (rating as any)?.characters_2v2 || {};

                // Pull PlayerStats for damage/ringouts/deaths (per-character badges)
                const playerStats = await PlayerStatsModel.findOne({ account_id: pid }).lean();
                const psChars1v1 = (playerStats as any)?.characters_1v1 || {};
                const psChars2v2 = (playerStats as any)?.characters_2v2 || {};

                // Get player's current character
                let character = "";
                try {
                  const conn = await redisClient.hGetAll(`connections:${pid}`);
                  character = conn?.character || "";
                  if (!character) {
                    const pd = await PlayerTesterModel.findById(pid).lean();
                    character = (pd as any)?.character || "character_wonder_woman";
                  }
                } catch {}

                const buildMode = (elo: number, wins: number, losses: number, rank: any, charMap: Record<string, any>, psCharMap: Record<string, any>) => {
                  if ((wins + losses) === 0 && Object.keys(charMap).length === 0) {
                    return { BestCharacter: { CurrentPoints: 0, MaxPoints: 0, GamesPlayed: 0, SetsPlayed: 0, CharacterSlug: "", LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) } }, DataByCharacter: {}, GamesPlayed: 0, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, SetsPlayed: 0, FinalLeaderboardRank: 0 };
                  }
                  const DataByCharacter: Record<string, any> = {};
                  let bestChar = character;
                  let bestElo = -1;
                  for (const [slug, data] of Object.entries(charMap)) {
                    const ce = (data as any).elo || 0;
                    const cw = (data as any).wins || 0;
                    const cl = (data as any).losses || 0;
                    const ps = psCharMap[slug] || {};
                    const damage = Math.round(ps.totalDamageDealt || 0);
                    const ringouts = ps.ringouts || 0;
                    DataByCharacter[slug] = {
                      CurrentPoints: ce, MaxPoints: ce,
                      GamesPlayed: cw + cl, SetsPlayed: cw + cl,
                      Wins: cw, Losses: cl,
                      DamageDealt: damage, DamageTaken: 0, Ringouts: ringouts, Deaths: 0,
                      LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                      LastDecayMs: 0,
                    };
                    if (ce > bestElo) { bestElo = ce; bestChar = slug; }
                  }
                  if (Object.keys(DataByCharacter).length === 0) {
                    const ps = psCharMap[character] || {};
                    DataByCharacter[character] = {
                      CurrentPoints: elo, MaxPoints: elo,
                      GamesPlayed: wins + losses, SetsPlayed: wins + losses,
                      Wins: wins, Losses: losses,
                      DamageDealt: Math.round(ps.totalDamageDealt || 0), DamageTaken: 0, Ringouts: ps.ringouts || 0, Deaths: 0,
                      LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                      LastDecayMs: 0,
                    };
                  }
                  return {
                    BestCharacter: {
                      CurrentPoints: bestElo, MaxPoints: bestElo,
                      GamesPlayed: wins + losses, SetsPlayed: wins + losses,
                      CharacterSlug: bestChar,
                      LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                    },
                    DataByCharacter,
                    GamesPlayed: wins + losses,
                    LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                    SetsPlayed: wins + losses,
                    FinalLeaderboardRank: rank?.rank || 0,
                  };
                };

                const rankedPayload = {
                  SeasonalData: {
                    "Season:SeasonFive": {
                      Ranked: {
                        DataByMode: {
                          "1v1": buildMode(elo1v1, wins1v1, losses1v1, rank1v1, chars1v1, psChars1v1),
                          "2v2": buildMode(elo2v2, wins2v2, losses2v2, rank2v2, chars2v2, psChars2v2),
                        },
                        ClaimedRewards: [],
                        bEndOfSeasonRewardsGranted: false,
                      },
                    },
                  },
                };

                client.send({
                  data: {
                    template_id: "FullRankUpdate",
                    ...rankedPayload,
                  },
                  payload: {
                    frm: { id: "internal-server", type: "server-api-key" },
                    template: "realtime",
                    account_id: pid,
                    profile_id: pid,
                  },
                  header: "",
                  cmd: "profile-notification",
                });
                logger.info(`[${serviceName}]: Sent FullRankUpdate to ${pid} (elo=${elo1v1})`);
              } catch (e) {
                logger.error(`[${serviceName}]: Error sending FullRankUpdate to ${pid}: ${e}`);
              }
            }
          } catch (e) {
            logger.error(`[${serviceName}]: Error processing set ELO: ${e}`);
          }

          // Clean up set state — use SET's player list, not the END_OF_MATCH notification's
          // (notification.playersIds may be from a different match linked via stale player_ranked_set)
          const setPlayerIds = existingSet
            ? [...new Set(existingSet.players.map((p: any) => p.playerId))] as string[]
            : notification.playersIds;
          for (const playerId of setPlayerIds) {
            await redisClient.del(`player_ranked_set:${playerId}`);
          }
          await redisClient.del(`ranked_set:${setId}`);
          await redisClient.del(`ranked_set_checkins:${setId}`);
          // Send MatchSetLeaverNotification + empty OnGameplayConfigNotified to return to menus
          for (const playerId of setPlayerIds) {
            const client = this.clients.get(playerId);
            if (client) {
              setTimeout(() => {
                client.send({
                  data: {
                    AccountId: playerId,
                    MatchId: notification.matchId,
                    template_id: "MatchSetLeaverNotification",
                  },
                  payload: {
                    frm: { id: "internal-server", type: "server-api-key" },
                    template: "realtime",
                    account_id: playerId,
                    profile_id: playerId,
                  },
                  header: "",
                  cmd: "profile-notification",
                });
              }, 1000);
              setTimeout(() => {
                client.send({
                  data: { MatchId: "", GameplayConfig: null, template_id: "OnGameplayConfigNotified" },
                  payload: { match: { id: "" }, custom_notification: "realtime" },
                  header: "", cmd: "update",
                });
                logger.info(`[${serviceName}]: Sent empty OnGameplayConfigNotified to ${playerId} (set complete, return to lobby)`);
              }, 1500);
            }
          }
        } else {
          // Set continues — store state and wait for check-ins
          const setState: any = {
            players: matchConfig.players,
            mode: matchConfig.mode,
            gamesPlayed,
            scores,
            checkins: [] as string[],
          };

          // If opponent dodged, mark set as conceded so check-in triggers immediate set-end
          if (dodgedByPlayer) {
            setState.conceded = true;
            setState.concedingPlayer = dodgedByPlayer;
            logger.info(`[${serviceName}]: Ranked set ${setId} — marked as conceded (dodge by ${dodgedByPlayer})`);
          }

          await redisClient.set(`ranked_set:${setId}`, JSON.stringify(setState), { EX: 600 });
          // Use the set's player list for mappings (matchConfig.players), not notification.playersIds
          // which could be from a different match linked via stale player_ranked_set
          const setPlayers = matchConfig.players.map((p) => p.playerId);
          for (const pid of setPlayers) {
            await redisClient.set(`player_ranked_set:${pid}`, setId, { EX: 600 });
          }
          logger.info(`[${serviceName}]: Ranked set ${setId} — game ${gamesPlayed}/3 complete (${scores[0]}-${scores[1]}), waiting for check-ins`);

          // Disconnect detection moved to handleRankedSetCheckin in routes.ts
          // When remaining player checks in or times out, it detects ranked_disconnect flag
        }
      } else {
        // Non-ranked or no config — send RematchDeclinedNotification after 1 second to kick to menus
        this.sendRematchDeclinedToPlayers(notification.playersIds, notification.matchId);
      }
    }

    // Preserve party lobby state ONLY if players are NOT in an active ranked set
    // Ranked sets handle their own match creation via checkins — party preservation
    // would interfere by creating matches through the lobby/rematch path instead
    const anyInRankedSet = await Promise.any(
      notification.playersIds.map(async (pid) => {
        const setId = await redisClient.get(`player_ranked_set:${pid}`);
        return !!setId;
      })
    ).catch(() => false);

    if (!anyInRankedSet) {
      this.handlePostMatchPartyPreservation(notification.playersIds);
    } else {
      logger.info(`[${serviceName}]: Skipping party preservation — players are in active ranked set`);
    }
  }

  sendRematchDeclinedToPlayers(playerIds: string[], matchId: string) {
    for (const playerId of playerIds) {
      const client = this.clients.get(playerId);
      if (client) {
        setTimeout(() => {
          const data = {
            data: {
              AccountId: playerId,
              MatchId: matchId,
              template_id: "RematchDeclinedNotification",
            },
            payload: {
              frm: {
                id: "internal-server",
                type: "server-api-key",
              },
              template: "realtime",
              account_id: playerId,
              profile_id: playerId,
            },
            header: "",
            cmd: "profile-notification",
          };
          client.send(data);
        }, 1000);
      }
    }
  }

  async handlePostMatchPartyPreservation(playerIds: string[]) {
    for (const playerId of playerIds) {
      try {
        const lobbyId = await redisGetPlayerLobby(playerId);
        if (!lobbyId) continue;

        const lobbyState = await redisGetLobbyState(lobbyId);
        if (!lobbyState || lobbyState.playerIds.length <= 1) continue;

        // Refresh TTLs so the lobby survives through long sessions
        await redisSaveLobbyState(lobbyId, lobbyState);
        await redisSavePlayerLobby(playerId, lobbyId);
        await redisClient.del(`party_ready:${lobbyId}`);

        // This player is in a party — preserve their lobby data through disconnect
        // so the REJOIN path in create_party_lobby works when they reconnect
        this.pendingRejoin.add(playerId);
        logger.info(
          `[${serviceName}]: Post-match: Marking player ${playerId} for party preservation (lobby ${lobbyId} with ${lobbyState.playerIds.length} players)`,
        );

        // Longer timeout for post-match (45 seconds — game takes time to transition
        // through end-of-match screens before disconnecting and reconnecting)
        setTimeout(async () => {
          if (this.pendingRejoin.has(playerId)) {
            // Check if the player is still connected — if so, they never disconnected,
            // so just clear the flag without wiping data
            const connectedClient = this.clients.get(playerId);
            if (connectedClient) {
              logger.info(
                `[${serviceName}]: Post-match: Player ${playerId} is still connected — clearing pending rejoin flag (no cleanup needed)`,
              );
              this.pendingRejoin.delete(playerId);
              return;
            }

            // Player actually disconnected and didn't reconnect — clean up
            logger.warn(
              `[${serviceName}]: Post-match: Player ${playerId} did not reconnect after match — cleaning up`,
            );
            this.pendingRejoin.delete(playerId);
            await redisRemoveOnlinePlayer(playerId);
            await redisCleanupPlayerLobby(playerId);
            await redisDeletePlayerKeys(playerId);
          }
        }, 45000); // 45 seconds timeout for post-match reconnection
      } catch (err) {
        logger.error(`[${serviceName}]: Post-match: Error checking party state for ${playerId}: ${err}`);
      }
    }
  }

  handleMatchMakingComplete(notification: RedisMatchMakingCompleteNotification) {
    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);
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
      if (client) {
        client.send(message);
      }
    }
  }

  private setupRedisSubscription() {
    // Subscribe to channels
    this.redisSub.subscribe(ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL, async (message) => {
      const notification = JSON.parse(message) as MATCH_FOUND_NOTIFICATION;
      await this.handleMatchFound(notification);
    });

    this.redisSub.subscribe(ALL_PERKS_LOCKED_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisAllPerksLockedNotification;
      this.handleAllPerksLocked(notification);
    });

    this.redisSub.subscribe(ON_MATCH_MAKER_STARTED_CHANNEL, async (message) => {
      const notification = JSON.parse(message) as ON_MATCH_MAKER_STARTED_NOTIFICATION;
      await this.handlePartyQueued(notification);
    });

    this.redisSub.subscribe(GAME_SERVER_INSTANCE_READY_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisGameServerInstanceReadyNotification;
      this.handleGameServerInstanceReady(notification);
    });

    this.redisSub.subscribe(MATCHMAKING_COMPLETE_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisMatchMakingCompleteNotification;
      this.handleMatchMakingComplete(notification);
    });

    this.redisSub.subscribe(ON_LOBBY_MODE_UPDATED, (message) => {
      const notification = JSON.parse(message) as RedisOnLobbyModeUpdatedNotification;
      this.handleOnLobbyModeChanged(notification);
    });

    this.redisSub.subscribe(ON_CANCEL_MATCHMAKING, async (message) => {
      const notification = JSON.parse(message) as RedisCancelMatchMakingNotification;
      await this.handleCancelMatchMaking(notification);
    });

    this.redisSub.subscribe(ON_END_OF_MATCH, async (message) => {
      const notification = JSON.parse(message) as RedisMatchEndNotification;
      await this.handleOnMatchEnd(notification);
    });

    // Party invite via Hydra WS using InviteReceivedForLobby template
    this.redisSub.subscribe(PARTY_INVITE_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisPartyInviteNotification;
      this.handlePartyInvite(notification);
    });

    this.redisSub.subscribe(PLAYER_JOINED_LOBBY_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisPlayerJoinedLobbyNotification;
      this.handlePlayerJoinedLobby(notification);
    });

    this.redisSub.subscribe(LOBBY_REJOIN_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisLobbyRejoinNotification;
      this.handleLobbyRejoin(notification);
    });

    this.redisSub.subscribe(LOBBY_TRANSITION_CHANNEL, async (message) => {
      const notification = JSON.parse(message) as RedisLobbyTransitionNotification;
      await this.handleLobbyTransition(notification);
    });

    this.redisSub.subscribe(TOAST_RECEIVED_CHANNEL, (message) => {
      logger.info(`[${serviceName}]: Toast subscriber callback fired, message: ${message}`);
      try {
        const notification = JSON.parse(message) as RedisToastNotification;
        this.handleToastReceived(notification).catch((err) => {
          logger.error(`[${serviceName}]: Unhandled error in handleToastReceived: ${err}`);
        });
      } catch (err) {
        logger.error(`[${serviceName}]: Error parsing toast message: ${err} — raw: ${message}`);
      }
    }).then(() => {
      logger.info(`[${serviceName}]: Subscribed to ${TOAST_RECEIVED_CHANNEL} successfully`);
    }).catch((err) => {
      logger.error(`[${serviceName}]: Failed to subscribe to ${TOAST_RECEIVED_CHANNEL}: ${err}`);
    });

    this.redisSub.subscribe(PLAYER_LOADOUT_LOCKED_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisPlayerLoadoutLockedNotification;
      this.handlePlayerLoadoutLocked(notification);
    });

    // Lobby return — send RematchDeclinedNotification to make the game
    // "return to lobby" and call create_party_lobby natively.
    // Used for party invite accept so invitee re-fetches the shared lobby.
    this.redisSub.subscribe(LOBBY_RETURN_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisLobbyReturnNotification;
      const client = this.clients.get(notification.playerId);
      if (client) {
        logger.info(`[${serviceName}]: Sending RematchDeclinedNotification to ${notification.playerId} for lobby return`);
        const data = {
          data: {
            AccountId: notification.playerId,
            MatchId: notification.matchId,
            template_id: "RematchDeclinedNotification",
          },
          payload: {
            frm: {
              id: "internal-server",
              type: "server-api-key",
            },
            template: "realtime",
            account_id: notification.playerId,
            profile_id: notification.playerId,
          },
          header: "",
          cmd: "profile-notification",
        };
        client.send(data);
      }
    });

    // NOTE: partyMemberJoinNotif subscription MOVED to AccelByteLobbyWsService.
    // The game processes AccelByte text-format messages on the AccelByte Lobby WS
    // (/lobby/ path), NOT on the Hydra WS. Sending it here had no effect because
    // the game ignores AccelByte-format text on this connection.

    // Friend request WS notification — sends native WBPNFriendRequestReceivedNotification
    this.redisSub.subscribe(FRIEND_REQUEST_WS_CHANNEL, (message) => {
      try {
        const notification = JSON.parse(message) as RedisFriendRequestWSNotification;
        const client = this.clients.get(notification.receiverAccountId);
        if (client) {
          logger.info(`[${serviceName}]: Sending WBPNFriendRequestReceivedNotification to ${notification.receiverAccountId}`);
          client.send({
            data: {
              template_id: "WBPNFriendRequestReceivedNotification",
              SenderWBPNAccountID: notification.senderAccountId,
              WBPNInvitationID: notification.invitationId,
            },
            payload: {
              match: {
                id: notification.receiverAccountId,
              },
              custom_notification: "realtime",
            },
            header: "",
            cmd: "profile-notification",
          });
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error handling friend request WS notification: ${e}`);
      }
    });

    // Custom lobby rematch decline — send RematchDeclinedNotification to all affected players
    this.redisSub.subscribe("custom_lobby_rematch_decline", (message) => {
      const { playerIds } = JSON.parse(message) as { playerIds: string[] };
      logger.info(`[${serviceName}]: Sending RematchDeclinedNotification to ${playerIds.length} players (custom lobby decline)`);
      for (const playerId of playerIds) {
        const client = this.clients.get(playerId);
        if (client) {
          const data = {
            data: {
              AccountId: playerId,
              MatchId: "",
              template_id: "RematchDeclinedNotification",
            },
            payload: {
              frm: {
                id: "internal-server",
                type: "server-api-key",
              },
              template: "realtime",
              account_id: playerId,
              profile_id: playerId,
            },
            header: "",
            cmd: "profile-notification",
          };
          client.send(data);
        }
      }
    });

    // Ranked set: concede — send MatchSetLeaverNotification to all players
    this.redisSub.subscribe("ranked_set:leaver", (message) => {
      const { playerIds, leaverPlayerId, matchId } = JSON.parse(message);
      logger.info(`[${serviceName}]: Sending MatchSetLeaverNotification to ${playerIds.length} players (leaver: ${leaverPlayerId})`);
      for (const playerId of playerIds) {
        const client = this.clients.get(playerId);
        if (client) {
          const data = {
            data: {
              AccountId: leaverPlayerId,
              MatchId: matchId,
              template_id: "MatchSetLeaverNotification",
            },
            payload: {
              frm: {
                id: "internal-server",
                type: "server-api-key",
              },
              template: "realtime",
              account_id: playerId,
              profile_id: playerId,
            },
            header: "",
            cmd: "profile-notification",
          };
          client.send(data);
          logger.info(`[${serviceName}]: Sent MatchSetLeaverNotification to ${playerId}`);

          // Follow up with empty OnGameplayConfigNotified to trigger HandleGameplayConfigParsedOrReturnToLobby
          setTimeout(() => {
            client.send({
              data: {
                MatchId: "",
                GameplayConfig: null,
                template_id: "OnGameplayConfigNotified",
              },
              payload: {
                match: { id: "" },
                custom_notification: "realtime",
              },
              header: "",
              cmd: "update",
            });
            logger.info(`[${serviceName}]: Sent empty OnGameplayConfigNotified to ${playerId} (return to lobby)`);
          }, 500);

          // FullRankUpdate is sent after ELO processing (both match-end and concede paths)
        }
      }
    });

    // FullRankUpdate from concede path (match-end path sends inline above)
    this.redisSub.subscribe("ranked_set:fullrankupdate", async (message) => {
      try {
        const { playerIds } = JSON.parse(message);
        logger.info(`[${serviceName}]: FullRankUpdate (concede) for ${playerIds.length} players`);
        for (const pid of playerIds) {
          const client = this.clients.get(pid);
          if (!client) continue;
          try {
            const rating = await getOrCreateRating(pid, "");
            const rank1v1 = await getPlayerRank(pid, "1v1");
            const rank2v2 = await getPlayerRank(pid, "2v2");
            const elo1v1 = rating?.elo_1v1 || 0;
            const elo2v2 = rating?.elo_2v2 || 0;
            const chars1v1 = (rating as any)?.characters_1v1 || {};
            const chars2v2 = (rating as any)?.characters_2v2 || {};
            // Pull PlayerStats for damage/ringouts
            const ps = await PlayerStatsModel.findOne({ account_id: pid }).lean();
            const psChars1v1 = (ps as any)?.characters_1v1 || {};
            const psChars2v2 = (ps as any)?.characters_2v2 || {};
            let character = "";
            try {
              const conn = await redisClient.hGetAll(`connections:${pid}`);
              character = conn?.character || "";
              if (!character) { const pd = await PlayerTesterModel.findById(pid).lean(); character = (pd as any)?.character || "character_wonder_woman"; }
            } catch {}

            const buildMode = (elo: number, wins: number, losses: number, rank: any, charMap: Record<string, any>, psCharMap: Record<string, any>) => {
              if ((wins + losses) === 0 && Object.keys(charMap).length === 0) {
                return { BestCharacter: { CurrentPoints: 0, MaxPoints: 0, GamesPlayed: 0, SetsPlayed: 0, CharacterSlug: "", LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) } }, DataByCharacter: {}, GamesPlayed: 0, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, SetsPlayed: 0, FinalLeaderboardRank: 0 };
              }
              const DataByCharacter: Record<string, any> = {};
              let bestChar = character; let bestElo = -1;
              for (const [slug, data] of Object.entries(charMap)) {
                const ce = (data as any).elo || 0; const cw = (data as any).wins || 0; const cl = (data as any).losses || 0;
                const psc = psCharMap[slug] || {};
                DataByCharacter[slug] = { CurrentPoints: ce, MaxPoints: ce, GamesPlayed: cw + cl, SetsPlayed: cw + cl, Wins: cw, Losses: cl, DamageDealt: Math.round(psc.totalDamageDealt || 0), DamageTaken: 0, Ringouts: psc.ringouts || 0, Deaths: 0, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, LastDecayMs: 0 };
                if (ce > bestElo) { bestElo = ce; bestChar = slug; }
              }
              if (Object.keys(DataByCharacter).length === 0) {
                const psc = psCharMap[character] || {};
                DataByCharacter[character] = { CurrentPoints: elo, MaxPoints: elo, GamesPlayed: wins + losses, SetsPlayed: wins + losses, Wins: wins, Losses: losses, DamageDealt: Math.round(psc.totalDamageDealt || 0), DamageTaken: 0, Ringouts: psc.ringouts || 0, Deaths: 0, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, LastDecayMs: 0 };
              }
              return { BestCharacter: { CurrentPoints: bestElo > 0 ? bestElo : elo, MaxPoints: bestElo > 0 ? bestElo : elo, GamesPlayed: wins + losses, SetsPlayed: wins + losses, CharacterSlug: bestChar, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) } }, DataByCharacter, GamesPlayed: wins + losses, LastUpdateTimestamp: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, SetsPlayed: wins + losses, FinalLeaderboardRank: rank?.rank || 0 };
            };

            client.send({
              data: {
                template_id: "FullRankUpdate",
                SeasonalData: { "Season:SeasonFive": { Ranked: { DataByMode: { "1v1": buildMode(elo1v1, rating?.wins_1v1 || 0, rating?.losses_1v1 || 0, rank1v1, chars1v1, psChars1v1), "2v2": buildMode(elo2v2, rating?.wins_2v2 || 0, rating?.losses_2v2 || 0, rank2v2, chars2v2, psChars2v2) }, ClaimedRewards: [], bEndOfSeasonRewardsGranted: false } } },
              },
              payload: { frm: { id: "internal-server", type: "server-api-key" }, template: "realtime", account_id: pid, profile_id: pid },
              header: "", cmd: "profile-notification",
            });
            logger.info(`[${serviceName}]: Sent FullRankUpdate to ${pid} (elo=${elo1v1}) [concede]`);
          } catch (e) {
            logger.error(`[${serviceName}]: Error sending FullRankUpdate to ${pid}: ${e}`);
          }
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error in ranked_set:fullrankupdate: ${e}`);
      }
    });

    // Ranked set: check-in — notify players that someone checked in
    this.redisSub.subscribe("ranked_set:checkin", (message) => {
      try {
        const { playerIds, checkedInPlayer, checkins } = JSON.parse(message);
        logger.info(`[${serviceName}]: Ranked set check-in notification: ${checkedInPlayer} (${checkins.length}/${playerIds.length})`);
        for (const playerId of playerIds) {
          const client = this.clients.get(playerId);
          if (client) {
            const data = {
              data: {
                CheckedInAccountId: checkedInPlayer,
                CheckedInCount: checkins.length,
                TotalPlayers: playerIds.length,
                template_id: "MatchSetCheckinNotification",
              },
              payload: {
                frm: {
                  id: "internal-server",
                  type: "server-api-key",
                },
                template: "realtime",
                account_id: playerId,
                profile_id: playerId,
              },
              header: "",
              cmd: "profile-notification",
            };
            client.send(data);
          }
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error handling ranked set checkin notification: ${e}`);
      }
    });

    // Custom lobby notifications (from new SSC-based lobby system)
    // Dispatches profile-notification / update messages to specific players
    this.redisSub.subscribe("custom_lobby:notification", (message) => {
      try {
        const msg = JSON.parse(message) as {
          targetPlayerIds: string[];
          excludePlayerIds: string[];
          notification: any;
        };
        const excludeSet = new Set(msg.excludePlayerIds || []);
        for (const playerId of msg.targetPlayerIds) {
          if (excludeSet.has(playerId)) continue;
          const client = this.clients.get(playerId);
          if (client) {
            client.send(msg.notification);
          }
        }
      } catch (e) {
        logger.error(`[${serviceName}]: Error handling custom lobby notification: ${e}`);
      }
    });
  }
}

// CHATGPT really figure this one out... Hats off to CHATGPT
export function parseInitHydraWebsocketMessage(buf: Buffer): { account: SharedTypes.IAccountToken; id: string } {
  // 1) JWT length prefix at byte 0x13 (19)
  const jwtLen = buf.readUInt16BE(0x13);

  // 2) extract JWT string
  const jwtStart = 0x15;
  const jwtEnd = jwtStart + jwtLen;
  const jwt = decodeToken(buf.toString("utf8", jwtStart, jwtEnd));

  // 3) extract the next 12 raw bytes as `id`
  // IT FIGURE OUT THAT THIS WAS AN ID!!! NEVER would of seen this one
  const idLenght = jwtEnd + 12;
  const idBuf = buf.subarray(jwtEnd, idLenght);
  const id = idBuf.toString("hex");

  // 4) now the TLV payload starts at the connection object:
  //    byte 0x1d1 is the MAP8 (0x60) that opens your
  //    { connection: { … } } block.
  const hydraObectStart = idLenght;

  // reuse your existing TLV decoder (from the last snippet)
  const rest = new HydraDecoder(buf.subarray(hydraObectStart)).readValue();

  return {
    account: jwt,
    id,
    ...rest,
  };
}
