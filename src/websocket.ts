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
  redisDeleteLobbyState,
  redisDeletePlayerLobby,
  redisPublishLobbyRejoin,
} from "./config/redis";
import { Server } from "https";
import { Server as HttpServer } from "http";
import { GAME_SERVER_PORT } from "./game/udp";
import { logger, logwrapper, BE_VERBOSE } from "./config/logger";
import { MVSTime } from "./utils/date";
import ObjectID from "bson-objectid";
import { RedisClientType } from "@redis/client";
import env from "./env/env";
import { Cosmetics, TauntSlotsClass, defaultTaunts, IDefaultTaunts } from "./database/Cosmetics";
import { getEquippedCosmetics } from "./services/cosmeticsService";

const serviceName: string = "WebSocket";
const logPrefix = `[${serviceName}]:`;

export class WebSocketPlayer {
  init: boolean = false;
  ws: WebSocket;
  deleted?: boolean;
  account: SharedTypes.IAccountToken | undefined;
  matchTick: NodeJS.Timeout | undefined;
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

interface GameplayConfig {
  ArenaModeInfo: null | string;
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

    redisAddOnlinePlayer(playerWS.account.id);
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
      // so remaining players aren't stuck with a ghost teammate
      try {
        const lobbyId = await redisGetPlayerLobby(playerId);
        logger.info(
          `[${serviceName}]: DISCONNECT-DIAG: Player ${playerId} disconnect handler running. ` +
          `lobbyId=${lobbyId || "NONE"}, pendingRejoin=${this.pendingRejoin.has(playerId)}, ` +
          `wsReadyState=${playerWS.ws.readyState}, deleted=${playerWS.deleted}`,
        );
        if (lobbyId) {
          const lobbyState = await redisGetLobbyState(lobbyId);
          logger.info(
            `[${serviceName}]: DISCONNECT-DIAG: Player ${playerId} lobby ${lobbyId} state: ` +
            `${lobbyState ? `playerIds=[${lobbyState.playerIds.join(",")}], mode=${(lobbyState as any).mode || "unknown"}` : "NULL (already deleted!)"}`,
          );
          if (lobbyState && lobbyState.playerIds.length > 1) {
            const otherPlayerIds = lobbyState.playerIds.filter(pid => pid !== playerId);
            logger.info(
              `[${serviceName}]: Player ${playerId} disconnected from multi-player lobby ${lobbyId} — disbanding party. Remaining players: ${otherPlayerIds.join(", ")}`,
            );

            // Delete the entire lobby
            await redisDeleteLobbyState(lobbyId);

            // Clear player_lobby for ALL players (including the disconnected one)
            for (const pid of lobbyState.playerIds) {
              await redisDeletePlayerLobby(pid);
            }

            // Force-rejoin remaining players so they get fresh solo lobbies
            for (const pid of otherPlayerIds) {
              const rejoinNotification: RedisLobbyRejoinNotification = {
                playerId: pid,
                lobbyId,
              };
              await redisPublishLobbyRejoin(rejoinNotification);
              logger.info(`[${serviceName}]: Triggered lobby rejoin for remaining player ${pid} after party disband`);
            }
          }
        }
      } catch (err) {
        logger.error(`[${serviceName}]: Error disbanding party for disconnected player ${playerId}: ${err}`);
      }

      playerWS.deleted = true;
      this.stopMatchTick(playerWS);
      this.attemptRemoveMatchTicket(playerWS);
      this.clients.delete(playerId);
      redisRemoveOnlinePlayer(playerId);
      redisCleanupPlayerLobby(playerId); // Clean up lobby state before deleting player keys
      redisDeletePlayerKeys(playerId);
      redisDeleteConnectionKeysByIp(playerWS.ip);
      logger.info(
        `[${serviceName}]: Player ${playerId} with IP ${playerWS.ip} and name ${playerWS.account.username} disconnected from websocket`,
      );
    }
  }

  attemptRemoveMatchTicket(playerWS: WebSocketPlayer) {
    if (playerWS.ticket) {
      redisPopMatchTicketsFromQueue("1v1", [playerWS.ticket]);
      redisPopMatchTicketsFromQueue("2v2", [playerWS.ticket]);
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

  stopMatchTick(player: WebSocketPlayer) {
    if (player.matchTick) {
      logger.info(
        `[${serviceName}]: Stopping matchtick for player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"}`,
      );
      clearInterval(player.matchTick);
      player.matchTick = undefined;
      // Match was found — mark as in_match so party join is still blocked
      if (player.account?.id) {
        redisUpdatePlayerStatus(player.account.id, "in_match");
      }
    }
  }

  async handlePartyQueued(notification: ON_MATCH_MAKER_STARTED_NOTIFICATION) {
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

    setTimeout(() => {
      this.cancelMatchMaking(client, notification.matchmakingRequestId);
    }, 100_000);
  }

  cancelMatchMaking(client: WebSocketPlayer, matchmakingRequestId: string) {
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
      this.attemptRemoveMatchTicket(client);
      clearInterval(client.matchTick);
      client.matchTick = undefined;
      client.send(message);
      // Clear the "queued" status so the player isn't stuck as searching
      if (client.account?.id) {
        redisUpdatePlayerStatus(client.account.id, "idle");
      }
      logger.trace(
        `[${serviceName}]: Canceling matchmaking - ${client.account?.id ?? "unknown"} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} - matchmakingRequestId: ${matchmakingRequestId}`,
      );
    }
  }

  handleMatchFound(notification: MATCH_FOUND_NOTIFICATION) {
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
          this.stopMatchTick(player);
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
    const MatchTime = 420;
    const NumRingouts = this.getNumRingouts(notification);

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

    const Players: { [key: string]: PlayerConfig } = {};

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

        Players[player.playerId] = {
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
          RankedDivision: null,
          bUseCharacterDisplayName: false,
          StartingDamage: 0,
          TeamIndex: player.teamIndex,
          ProfileIcon: profileIcon,
          WinStreak: null,
          RankedTier: null,
          Handicap: 0,
          RingoutVfx: playerConfig.RingoutVfx ?? "ring_out_vfx_default",
          Character: character,
          Banner: playerConfig.Banner ?? "banner_default",
          StatTrackers: [
            [
              statTrackerSlots[0],
              1,
            ],
            [
              statTrackerSlots[1],
              1,
            ],
            [
              statTrackerSlots[2],
              1,
            ],
          ],
          Perks: [],
          PlayerIndex: player.playerIndex,
          PartyId: player.partyId,
          // Username: { default: rPlayerConnectionByID.username || "Player" },
          Username: {},
          Buffs: [],
          Skin: skin,
          BotDifficultyMin: 0,
        };

        logger.info(`${logPrefix} Successfully created player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}`);
      }
      catch (error) {
        logger.error(
          `[${serviceName}]: Error creating player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} : ${JSON.stringify(error)}`,
        );
        // logger.error(`${logPrefix} Character: ${character}, PlayerConfig.Taunts: ${JSON.stringify(playerConfig.Taunts)}`);
        // logger.error(`${logPrefix} PlayerConfig.StatTrackers: ${JSON.stringify(playerConfig.StatTrackers)}`);

        // Create a minimal valid config as fallback
        Players[player.playerId] = {
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
          Buffs: [],
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
      logger.info(
        `[${serviceName}]: Prepared player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}, GameplayPreferences: ${Players[player.playerId].GameplayPreferences}`,
      );
    }

    // Create the message to send to the players

    let stageHazards: boolean = notification.map.toLowerCase() === "PVE_03".toLowerCase() ? true : false;
    const message: GameNotification = {
      data: {
        MatchId: notification.matchId,
        GameplayConfig: {
          ArenaModeInfo: null,
          RiftNodeId: "",
          ScoreEvaluationRule: "TargetScoreIsWin",
          bIsPvP: true,
          ScoreAttributionRule: "AttributeToAttacker",
          MatchDurationSeconds: MatchTime,
          Created: {
            _hydra_unix_date: MVSTime(new Date()),
          },
          EventQueueSlug: "",
          bModeGrantsProgress: true,
          TeamData: [],
          Spectators: {},
          bIsRanked: false,
          bIsCustomGame: false,
          Players,
          CustomGameSettings: {
            bHazardsEnabled: stageHazards,
            bShieldsEnabled: true,
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
          WorldBuffs: [],
          bIsTutorial: false,
          MatchId: notification.matchId,
          bIsOnlineMatch: true,
          ModeString: notification.mode,
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
  }

  async handleGameServerInstanceReady(notification: RedisGameServerInstanceReadyNotification) {
    let useCentralRollback = env.USE_INTERNAL_ROLLBACK === 1 ? true : false;
    let rollbackHost = useCentralRollback ? `${env.UDP_SERVER_IP}` : "127.0.0.1";
    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);

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

  handleCancelMatchMaking(notification: RedisCancelMatchMakingNotification) {
    for (const playerId of notification.playersIds) {
      const client = this.clients.get(playerId);

      if (client) {
        this.cancelMatchMaking(client, notification.matchmakingId);
      }
    }
  }

  handlePartyInvite(notification: RedisPartyInviteNotification) {
    const client = this.clients.get(notification.invitedAccountId);
    if (client) {
      logger.info(
        `[${serviceName}]: Sending party invite to ${notification.invitedAccountId} from ${notification.inviterAccountId}`,
      );

      const invitationToken = randomUUID();
      const notificationId = randomUUID();

      // ============================================================
      // APPROACH 1: "profile-notification" cmd
      // This matches the same delivery mechanism used for EndOfMatchPayload
      // and RematchDeclinedNotification, which are proven to work.
      // The game's UHydraNotificationManager processes these with
      // NotificationTemplateID, NotificationID, NotificationData fields.
      // ============================================================
      const profileNotifMessage = {
        data: {
          template_id: "MatchInviteNotification",
          sender_id: notification.inviterAccountId,
          party_id: notification.lobbyId,
          party_type_slug: "player",
          InvitedAcccountID: notification.invitedAccountId, // triple 'c' matches game binary typo
          invitationToken: invitationToken,
          InviterAccountId: notification.inviterAccountId,
          InviterUsername: notification.inviterUsername,
          LobbyId: notification.lobbyId,
        },
        payload: {
          frm: {
            id: notification.inviterAccountId,
            type: "account",
          },
          template: "realtime",
          account_id: notification.invitedAccountId,
          profile_id: notification.invitedAccountId,
        },
        header: "",
        cmd: "profile-notification",
      };
      client.send(profileNotifMessage);
      logger.info(`[${serviceName}]: Sent "profile-notification" cmd with template_id="MatchInviteNotification" to ${notification.invitedAccountId}`);

      // ============================================================
      // APPROACH 2: "party-invite-player" Hydra cmd
      // This is the dedicated Hydra WS command for party invites.
      // Restructured with data containing notification fields and
      // payload matching the party state structure from the binary.
      // ============================================================
      const partyInviteMessage = {
        data: {
          sender_id: notification.inviterAccountId,
          party_id: notification.lobbyId,
          party_type_slug: "player",
          invitees: [notification.invitedAccountId],
          InvitedAcccountID: notification.invitedAccountId,
          invitationToken: invitationToken,
        },
        payload: {
          frm: {
            id: notification.inviterAccountId,
            type: "account",
          },
          to: {
            id: notification.invitedAccountId,
            type: "account",
          },
          party: {
            id: notification.lobbyId,
            leader_id: notification.inviterAccountId,
            party_id: notification.lobbyId,
            party_type_slug: "player",
            Leader: notification.inviterAccountId,
            Joined: [notification.inviterAccountId],
            DeclinedInvite: [],
            Invited: [notification.invitedAccountId],
          },
          match: {
            id: notification.lobbyId,
          },
          sender_id: notification.inviterAccountId,
          party_id: notification.lobbyId,
        },
        header: "",
        cmd: "party-invite-player",
      };
      client.send(partyInviteMessage);
      logger.info(`[${serviceName}]: Sent Hydra cmd="party-invite-player" to ${notification.invitedAccountId}`);

      // ============================================================
      // APPROACH 3: "update" cmd with MatchInviteNotification template
      // Same as before but kept as additional attempt.
      // ============================================================
      const updateMessage = {
        data: {
          template_id: "MatchInviteNotification",
          sender_id: notification.inviterAccountId,
          party_id: notification.lobbyId,
          party_type_slug: "player",
          InvitedAcccountID: notification.invitedAccountId,
          invitationToken: invitationToken,
          InviterAccountId: notification.inviterAccountId,
          InviterUsername: notification.inviterUsername,
          LobbyId: notification.lobbyId,
        },
        payload: {
          match: {
            id: notification.lobbyId,
          },
          custom_notification: "realtime",
        },
        header: "",
        cmd: "update",
      };
      client.send(updateMessage);
      logger.info(`[${serviceName}]: Sent "update" cmd with template_id="MatchInviteNotification" to ${notification.invitedAccountId}`);

    } else {
      logger.warn(
        `[${serviceName}]: Could not find player ${notification.invitedAccountId} to send party invite from ${notification.inviterAccountId}`,
      );
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

    // Build per-player data from Redis
    const gameplayPrefs: any = {};
    const autoPartyPrefs: any = {};
    const platforms: any = {};
    const lockedLoadouts: any = {};
    for (const pid of allPlayerIds) {
      const conn = await redisClient.hGetAll(`connections:${pid}`) as any;
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

    // Send lobby state to ALL players using multiple approaches
    for (const playerId of allPlayerIds) {
      const client = this.clients.get(playerId);
      if (client) {
        // 1) "lobby-join" — dedicated cmd found in game binary (same hyphenated format
        //    as game-server-instance-ready, matchmaking-complete, party-invite-player).
        //    Follows the pattern: data={}, all data in payload.
        const lobbyJoinMsg = {
          data: {},
          payload: {
            lobby: lobbyData,
            match: { id: lobbyId },
            party_id: lobbyId,
          },
          header: "",
          cmd: "lobby-join",
        };
        client.send(lobbyJoinMsg);
        logger.info(`[${serviceName}]: Sent lobby-join to ${playerId} for lobby ${lobbyId}`);

        // 2) OnLobbyRuntimeDataUpdated — found in binary, specifically for updating
        //    lobby runtime data (player roster, mode, etc.)
        const runtimeUpdateMsg = {
          data: {
            template_id: "OnLobbyRuntimeDataUpdated",
            LobbyId: lobbyId,
            ModeString: notification.mode,
            lobby: lobbyData,
          },
          payload: { match: { id: lobbyId }, custom_notification: "realtime" },
          header: "",
          cmd: "update",
        };
        client.send(runtimeUpdateMsg);
        logger.info(`[${serviceName}]: Sent OnLobbyRuntimeDataUpdated to ${playerId} for lobby ${lobbyId}`);

        // 3) PlayerJoinedLobby — found in binary as a direct event name
        const playerJoinedMsg = {
          data: {
            template_id: "PlayerJoinedLobby",
            LobbyId: lobbyId,
            ModeString: notification.mode,
            lobby: lobbyData,
            JoinedPlayerId: notification.joinedPlayerId,
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
      setTimeout(() => {
        if (this.pendingRejoin.has(playerId)) {
          logger.warn(`[${serviceName}]: Player ${playerId} did not reconnect after rejoin — clearing pending flag`);
          this.pendingRejoin.delete(playerId);
          // Clean up since they didn't reconnect
          redisRemoveOnlinePlayer(playerId);
          redisCleanupPlayerLobby(playerId);
          redisDeletePlayerKeys(playerId);
        }
      }, 15000); // 15 seconds timeout for reconnection
    }, 500); // 500ms delay before closing — minimal disruption
  }

  handleOnMatchEnd(notification: RedisMatchEndNotification) {
    for (const playerId of notification.playersIds) {
      const client = this.clients.get(playerId);

      if (client) {
        const data = {
          data: {
            GameplayConfig: client.matchConfig?.data.GameplayConfig,
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
        //logger.info(`${logPrefix} END OF MATCH WS: ${JSON.stringify(data)}`);
        logger.info(`${logPrefix} Received End of Match for MatchID: ${client.matchConfig?.data.GameplayConfig.MatchId}`);
        logwrapper.verbose(`[${serviceName}]: End of Match data for match: ${JSON.stringify(data)}`);
        client.send(data);

        // Clear matchConfig and status since the match is over
        client.matchConfig = undefined;
        redisUpdatePlayerStatus(playerId, "idle");

        setTimeout(() => {
          const data = {
            data: {
              AccountId: playerId,
              MatchId: notification.matchId,
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

    // Preserve party lobby state for players who were in a multi-player lobby
    // so they rejoin the same party after the match ends
    this.handlePostMatchPartyPreservation(notification.playersIds);
  }

  async handlePostMatchPartyPreservation(playerIds: string[]) {
    for (const playerId of playerIds) {
      try {
        const lobbyId = await redisGetPlayerLobby(playerId);
        if (!lobbyId) continue;

        const lobbyState = await redisGetLobbyState(lobbyId);
        if (!lobbyState || lobbyState.playerIds.length <= 1) continue;

        // This player is in a party — preserve their lobby data through disconnect
        // so the REJOIN path in create_party_lobby works when they reconnect
        this.pendingRejoin.add(playerId);
        logger.info(
          `[${serviceName}]: Post-match: Marking player ${playerId} for party preservation (lobby ${lobbyId} with ${lobbyState.playerIds.length} players)`,
        );

        // Longer timeout for post-match (45 seconds — game takes time to transition
        // through end-of-match screens before disconnecting and reconnecting)
        setTimeout(() => {
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
            redisRemoveOnlinePlayer(playerId);
            redisCleanupPlayerLobby(playerId);
            redisDeletePlayerKeys(playerId);
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
    this.redisSub.subscribe(ON_GAMEPLAY_CONFIG_NOTIFIED_CHANNEL, (message) => {
      const notification = JSON.parse(message) as MATCH_FOUND_NOTIFICATION;
      this.handleMatchFound(notification);
    });

    this.redisSub.subscribe(ALL_PERKS_LOCKED_CHANNEL, (message) => {
      const notification = JSON.parse(message) as RedisAllPerksLockedNotification;
      this.handleAllPerksLocked(notification);
    });

    this.redisSub.subscribe(ON_MATCH_MAKER_STARTED_CHANNEL, (message) => {
      const notification = JSON.parse(message) as ON_MATCH_MAKER_STARTED_NOTIFICATION;
      this.handlePartyQueued(notification);
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

    this.redisSub.subscribe(ON_CANCEL_MATCHMAKING, (message) => {
      const notification = JSON.parse(message) as RedisCancelMatchMakingNotification;
      this.handleCancelMatchMaking(notification);
    });

    this.redisSub.subscribe(ON_END_OF_MATCH, (message) => {
      const notification = JSON.parse(message) as RedisMatchEndNotification;
      this.handleOnMatchEnd(notification);
    });

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
