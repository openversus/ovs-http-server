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
const logPrefix: string = `[${serviceName}]:`;

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
    this.clients.set(playerWS.account.id, playerWS);
    logger.info(
      `${logPrefix} Player ${playerWS.account.id} with IP ${playerWS.ip} and name ${playerWS.account.username} connected to websocket`,
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

  handleDisconnect(playerWS: WebSocketPlayer) {
    if (playerWS && playerWS.account) {
      playerWS.deleted = true;
      this.stopMatchTick(playerWS);
      this.attemptRemoveMatchTicket(playerWS);
      this.clients.delete(playerWS.account.id);
      redisDeletePlayerKeys(playerWS.account.id);
      redisDeleteConnectionKeysByIp(playerWS.ip);
      logger.info(
        `${logPrefix} Player ${playerWS.account.id} with IP ${playerWS.ip} and name ${playerWS.account.username} disconnected from websocket`,
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

      ws.on("close", () => {
        this.handleDisconnect(playerWS);
      });

      ws.on("error", (error) => {
        logger.error(
          `${logPrefix} WebSocket error for player ${playerWS.account?.id ?? "unknown"} with IP ${playerWS.ip} and name ${playerWS.account?.username ?? "unknown"}, error: ${JSON.stringify(error)}`,
        );
      });
    });
  }

  stopMatchTick(player: WebSocketPlayer) {
    if (player.matchTick) {
      logger.info(
        `${logPrefix} Stopping matchtick for player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"}`,
      );
      clearInterval(player.matchTick);
      player.matchTick = undefined;
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
      logger.trace(
        `${logPrefix} Canceling matchmaking - ${client.account?.id ?? "unknown"} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} - matchmakingRequestId: ${matchmakingRequestId}`,
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
          `${logPrefix} Error getting player from clients map for player ${matchPlayer.playerId} with IP ${matchPlayer.ip}, error: ${JSON.stringify(error)}`,
        );
      }

      if (!tempPlayer) {
        logger.error(
          `${logPrefix} Could not find player ${matchPlayer.playerId} with IP ${matchPlayer.ip} to prepare match found notification for match ${notification.matchId}`,
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
            `${logPrefix} Error stopping match tick for player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"}`,
            error,
          );
        }

        const gameServerPort = notification.rollbackPort || GAME_SERVER_PORT;
        logger.info(
          `${logPrefix} Match ${notification.matchId} found for player ${player.account?.id ?? "unknown"}, sending match found notification with game server port ${gameServerPort}`,
        );
        const message = {
          data: {
            MatchKey: notification.matchKey,
            MatchID: notification.matchId,
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

        try {
          player.send(message);
        }
        catch (error) {
          logger.error(
            `${logPrefix} Error sending match found notification to player ${player.account?.id ?? "unknown"} with IP ${player.ip} and name ${player.account?.username ?? "unknown"} for match ${notification.matchId}, error: ${JSON.stringify(error)}`,
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

      const rawCosmetics = await redisClient.hGetAll(`connections:${playerId}:cosmetics`);
      const matchPlayer = (await redisClient.hGetAll(`connections:${playerId}`)) as unknown as RedisPlayerConnection;

      if (!rawCosmetics || Object.keys(rawCosmetics).length === 0) {
        logger.warn(
          `${logPrefix} No cosmetics found for player ${playerId} with IP ${matchPlayer.current_ip} and name ${matchPlayer.username ?? "unknown"} in Redis, fetching from database`,
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
              `${logPrefix} Failed to parse cosmetic field ${key} for player ${playerId} with IP ${matchPlayer.current_ip} and name ${matchPlayer.username ?? "unknown"}, error: ${JSON.stringify(e)}. This should not happen, as all cosmetics should be stored as JSON strings in Redis. Assigning default value for this field.`,
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
      const rPlayerConnectionByID = await redisClient.hGetAll(`connections:${player.playerId}`) as unknown as RedisPlayerConnection;
      const playerLoadout = playerLoadouts[i];
      const playerConfig = playerConfigs[i];
      const profileIcon = rPlayerConnectionByID.profileIcon ?? "profile_icon_default_gold";
      const character = rPlayerConnectionByID.character ?? "character_jason";
      const skin = rPlayerConnectionByID.skin ?? "skin_jason_000";
      const GameplayPreferences: number = Number(rPlayerConnectionByID.GameplayPreferences) || 964;

      logger.info(
        `${logPrefix} Building config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}, GameplayPreferences: ${GameplayPreferences}`,
      );

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
          Username: {},
          Buffs: [],
          Skin: skin,
          BotDifficultyMin: 0,
        };

        logger.info(`${logPrefix} Successfully created player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}`);
      }
      catch (error) {
        logger.error(
          `${logPrefix} Error creating player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} : ${JSON.stringify(error)}`,
        );

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
          Username: {},
          Buffs: [],
          Skin: skin,
          BotDifficultyMin: 0,
        };
      }

      logger.info(
        `${logPrefix} Prepared player config for player ${player.playerId} with IP ${rPlayerConnectionByID.current_ip} and name ${rPlayerConnectionByID.username ?? "unknown"} for match ${notification.matchId}, GameplayPreferences: ${Players[player.playerId].GameplayPreferences}`,
      );
    }

    // Create the message to send to the players
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
            bHazardsEnabled: false,
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
          bAllowMapHazards: false,
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
          `${logPrefix} Sent gameplay config to player ${client.account?.id ?? "unknown"} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.matchId}`,
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
              `${logPrefix} Match Perks are incomplete!!! This should not really happen for player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"}`,
            );
          }
        }
      }
    }
    for (const playerId of notification.playerIds) {
      const client = this.clients.get(playerId);
      if (client && client.matchConfig) {
        client.send(client.matchConfig);
        logwrapper.verbose(JSON.stringify(client.matchConfig.data.GameplayConfig.Players, null, 2));
        logger.info(
          `${logPrefix} Sent all perks lock to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.containerMatchId}`,
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
        `${logPrefix} Received game server instance ready for match ${notification.containerMatchId} and player ${playerId} with IP ${client?.ip ?? "unknown"} and name ${client?.account?.username ?? "unknown"}, sending game server info with port ${gameServerPort}`,
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
          `${logPrefix} Sent game server instance ready to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for match ${notification.containerMatchId}`,
        );
        client.send(message);
      }
      else {
        logger.error(
          `${logPrefix} Could not find player ${playerId} to send game server instance ready message for match ${notification.containerMatchId}`,
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
          `${logPrefix} OnLobbyModeUpdated send to player ${playerId} with IP ${client.ip} and name ${client.account?.username ?? "unknown"} for lobby ${notification.lobbyId} with mode ${notification.modeString}`,
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
        logger.info(`${logPrefix} Received End of Match for MatchID: ${client.matchConfig?.data.GameplayConfig.MatchId}`);
        logwrapper.verbose(`${logPrefix} End of Match data for match: ${JSON.stringify(data)}`);
        client.send(data);
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
