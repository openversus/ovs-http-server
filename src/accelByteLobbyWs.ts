import { WebSocket, WebSocketServer } from "ws";
import { Server as HttpServer } from "http";
import { IncomingMessage } from "http";
import { Socket } from "net";
import { logger } from "./config/logger";
import { decodeToken } from "./middleware/auth";
import {
  redisClient,
  RedisPlayerConnection,
  initRedisSubscriber,
  RedisClient,
  PARTY_INVITE_CHANNEL,
  RedisPartyInviteNotification,
} from "./config/redis";
import { randomUUID } from "crypto";

const serviceName = "AccelByteLobbyWS";
const logPrefix = `[${serviceName}]:`;

/**
 * AccelByte Lobby WebSocket Service
 *
 * The AccelByte SDK uses a separate text-based WebSocket connection at /lobby/
 * for social features like party invites, friend presence, matchmaking notifications.
 *
 * Protocol: Text-based YAML-like key-value format
 * Each message is a series of "key: value" lines separated by newlines.
 * The first line is always "type: <MessageType>"
 *
 * Message types:
 * - Request:  "<Name>Request"  (client → server)
 * - Response: "<Name>Response" (server → client, reply to request)
 * - Notification: "<Name>Notif" (server → client, unsolicited)
 *
 * Party invite notification (partyGetInvitedNotif):
 *   type: partyGetInvitedNotif
 *   from: <inviterUserId>
 *   partyId: <partyId>
 *   invitationToken: <token>
 */

interface AccelByteLobbyClient {
  ws: WebSocket;
  playerId: string;
  username: string;
}

export class AccelByteLobbyWsService {
  private wss: WebSocketServer;
  private clients: Map<string, AccelByteLobbyClient> = new Map(); // playerId → client
  private redisSub: RedisClient;

  constructor(httpServer: HttpServer) {
    // Create a WebSocket server with noServer so we can handle the upgrade manually
    this.wss = new WebSocketServer({ noServer: true });

    this.redisSub = initRedisSubscriber();

    // Handle upgrade requests on the HTTP server
    httpServer.on("upgrade", (request: IncomingMessage, socket: Socket, head: Buffer) => {
      const url = new URL(request.url || "", `http://${request.headers.host}`);
      const pathname = url.pathname;

      logger.info(`${logPrefix} WebSocket upgrade request: ${pathname}`);

      // Only handle /lobby/ path (AccelByte Lobby WebSocket)
      if (pathname === "/lobby/" || pathname === "/lobby") {
        this.wss.handleUpgrade(request, socket, head, (ws) => {
          this.wss.emit("connection", ws, request);
        });
      } else {
        // Let other WebSocket upgrade requests pass through (e.g. Hydra WS on different port)
        // Don't destroy the socket here — other handlers might need it
        logger.info(`${logPrefix} Ignoring WebSocket upgrade for non-lobby path: ${pathname}`);
      }
    });

    this.wss.on("connection", (ws: WebSocket, request: IncomingMessage) => {
      this.handleConnection(ws, request);
    });

    this.setupRedisSubscription();

    logger.info(`${logPrefix} AccelByte Lobby WebSocket service initialized (listening for /lobby/ upgrades)`);
  }

  private async handleConnection(ws: WebSocket, request: IncomingMessage) {
    const url = new URL(request.url || "", `http://${request.headers.host}`);
    const ip = request.socket.remoteAddress?.replace(/^::ffff:/, "") || "unknown";

    logger.info(`${logPrefix} New AccelByte Lobby WebSocket connection from ${ip}`);

    // Try to extract auth token from:
    // 1. Authorization header (Bearer token)
    // 2. Query parameter (token=xxx)
    let playerId = "";
    let username = "";

    const authHeader = request.headers["authorization"];
    const queryToken = url.searchParams.get("token");
    const token = authHeader?.replace("Bearer ", "") || queryToken || "";

    if (token) {
      try {
        const decoded = decodeToken(token);
        if (decoded && decoded.id) {
          playerId = decoded.id;
          username = decoded.username || "";
          logger.info(`${logPrefix} Authenticated player ${playerId} (${username}) via token`);
        }
      } catch (e) {
        logger.warn(`${logPrefix} Failed to decode token from AccelByte Lobby WS: ${e}`);
      }
    }

    // If no token auth, try to identify by IP from Redis connections
    if (!playerId) {
      try {
        const conn = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
        if (conn && conn.id) {
          playerId = conn.id;
          username = conn.username || conn.hydraUsername || "";
          logger.info(`${logPrefix} Identified player ${playerId} (${username}) by IP ${ip}`);
        }
      } catch (e) {
        logger.warn(`${logPrefix} Could not identify player by IP ${ip}: ${e}`);
      }
    }

    if (!playerId) {
      logger.warn(`${logPrefix} Could not identify player for AccelByte Lobby WS connection from ${ip} — keeping connection open for late identification`);
    }

    const client: AccelByteLobbyClient = { ws, playerId, username };

    if (playerId) {
      this.clients.set(playerId, client);
    }

    // Send a connected notification (AccelByte SDK expects this)
    this.sendMessage(ws, {
      type: "connectNotif",
      lobbySessionId: randomUUID(),
    });

    ws.on("message", (data: Buffer | string) => {
      const message = typeof data === "string" ? data : data.toString("utf-8");
      this.handleMessage(client, message, ip);
    });

    ws.on("close", () => {
      logger.info(`${logPrefix} AccelByte Lobby WS disconnected: ${playerId || ip}`);
      if (client.playerId) {
        this.clients.delete(client.playerId);
      }
    });

    ws.on("error", (err) => {
      logger.error(`${logPrefix} AccelByte Lobby WS error for ${playerId || ip}: ${err}`);
    });
  }

  /**
   * Parse an AccelByte text-format message into key-value pairs
   * Format: "key: value\nkey2: value2\n..."
   */
  private parseMessage(raw: string): Record<string, string> {
    const result: Record<string, string> = {};
    const lines = raw.split("\n");
    for (const line of lines) {
      const colonIndex = line.indexOf(": ");
      if (colonIndex > 0) {
        const key = line.substring(0, colonIndex).trim();
        const value = line.substring(colonIndex + 2).trim();
        result[key] = value;
      }
    }
    return result;
  }

  /**
   * Serialize key-value pairs into AccelByte text format
   */
  private serializeMessage(fields: Record<string, string>): string {
    return Object.entries(fields)
      .map(([key, value]) => `${key}: ${value}`)
      .join("\n");
  }

  /**
   * Send a text-format message to a WebSocket
   */
  private sendMessage(ws: WebSocket, fields: Record<string, string>) {
    if (ws.readyState === WebSocket.OPEN) {
      const message = this.serializeMessage(fields);
      logger.info(`${logPrefix} Sending message:\n${message}`);
      ws.send(message);
    }
  }

  private handleMessage(client: AccelByteLobbyClient, raw: string, ip: string) {
    logger.info(`${logPrefix} Received from ${client.playerId || ip}: ${raw}`);

    const parsed = this.parseMessage(raw);
    const type = parsed.type || "";

    // Late identification: if the message has an id field and we don't have a playerId yet
    if (!client.playerId && parsed.id) {
      client.playerId = parsed.id;
      this.clients.set(parsed.id, client);
      logger.info(`${logPrefix} Late-identified player ${parsed.id} from message`);
    }

    switch (type) {
      // Party request handlers
      case "partyInfoRequest":
        this.handlePartyInfoRequest(client, parsed);
        break;

      case "partyInviteRequest":
        this.handlePartyInviteRequest(client, parsed);
        break;

      case "partyJoinRequest":
        this.handlePartyJoinRequest(client, parsed);
        break;

      case "partyCreateRequest":
        this.handlePartyCreateRequest(client, parsed);
        break;

      // Friend/presence handlers
      case "setUserStatusRequest":
        this.handleSetUserStatus(client, parsed);
        break;

      case "friendsStatusRequest":
        this.handleFriendsStatus(client, parsed);
        break;

      case "listOnlineFriendsRequest":
        this.handleListOnlineFriends(client, parsed);
        break;

      default:
        logger.info(`${logPrefix} Unhandled AccelByte Lobby message type: ${type}`);
        // Send a generic OK response for unhandled request types
        if (type.endsWith("Request")) {
          const responseType = type.replace("Request", "Response");
          this.sendMessage(client.ws, {
            type: responseType,
            id: parsed.id || "0",
            code: "0", // 0 = success in AccelByte
          });
        }
        break;
    }
  }

  // ============================================================
  // Request Handlers
  // ============================================================

  private handlePartyInfoRequest(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    // Return empty party info — player is not in a party via AccelByte
    this.sendMessage(client.ws, {
      type: "partyInfoResponse",
      id: parsed.id || "0",
      code: "0",
      partyId: "",
      leaderId: client.playerId,
      members: client.playerId,
      invitees: "",
      invitationToken: "",
    });
  }

  private handlePartyCreateRequest(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    const partyId = randomUUID();
    this.sendMessage(client.ws, {
      type: "partyCreateResponse",
      id: parsed.id || "0",
      code: "0",
      partyId: partyId,
      leaderId: client.playerId,
      members: client.playerId,
      invitees: "",
      invitationToken: "",
    });
  }

  private handlePartyInviteRequest(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    const friendId = parsed.friendID || "";
    logger.info(`${logPrefix} Party invite request from ${client.playerId} to ${friendId}`);

    // Send response to the inviter
    this.sendMessage(client.ws, {
      type: "partyInviteResponse",
      id: parsed.id || "0",
      code: "0",
    });

    // Send notification to the inviter that the invite was sent
    this.sendMessage(client.ws, {
      type: "partyInviteNotif",
      inviterID: client.playerId,
      inviteeID: friendId,
    });

    // Send the invite notification to the invited player
    this.sendPartyInviteNotification(
      client.playerId,
      friendId,
      randomUUID(), // partyId
      randomUUID(), // invitationToken
    );
  }

  private handlePartyJoinRequest(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    const partyId = parsed.partyId || "";
    const invitationToken = parsed.invitationToken || "";

    logger.info(`${logPrefix} Party join request from ${client.playerId} for party ${partyId}`);

    this.sendMessage(client.ws, {
      type: "partyJoinResponse",
      id: parsed.id || "0",
      code: "0",
      partyId: partyId,
      leaderId: "",
      members: client.playerId,
      invitees: "",
      invitationToken: "",
    });
  }

  private handleSetUserStatus(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    this.sendMessage(client.ws, {
      type: "setUserStatusResponse",
      id: parsed.id || "0",
      code: "0",
    });
  }

  private handleFriendsStatus(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    this.sendMessage(client.ws, {
      type: "friendsStatusResponse",
      id: parsed.id || "0",
      code: "0",
      friendsId: "[]",
      availability: "[]",
      activity: "[]",
      lastSeenAt: "[]",
    });
  }

  private handleListOnlineFriends(client: AccelByteLobbyClient, parsed: Record<string, string>) {
    // === HIDDEN: Online friends list disabled until DLL change is made ===
    // Once the DLL supports proper friend invites, re-enable the block below.
    this.sendMessage(client.ws, {
      type: "listOnlineFriendsResponse",
      id: parsed.id || "0",
      code: "0",
      friendsId: "[]",
      availability: "[]",
      activity: "[]",
      lastSeenAt: "[]",
    });

    /*
    // Return connected players as "online friends"
    const onlineFriendIds: string[] = [];
    for (const [pid] of this.clients) {
      if (pid !== client.playerId) {
        onlineFriendIds.push(pid);
      }
    }

    this.sendMessage(client.ws, {
      type: "listOnlineFriendsResponse",
      id: parsed.id || "0",
      code: "0",
      friendsId: `[${onlineFriendIds.join(",")}]`,
      availability: `[${onlineFriendIds.map(() => "1").join(",")}]`,
      activity: `[${onlineFriendIds.map(() => "playing").join(",")}]`,
      lastSeenAt: `[${onlineFriendIds.map(() => new Date().toISOString()).join(",")}]`,
    });
    */
  }

  // ============================================================
  // Notification Senders
  // ============================================================

  /**
   * Send partyGetInvitedNotif to the invited player.
   * This is the AccelByte notification that triggers the in-game party invite popup.
   */
  sendPartyInviteNotification(fromPlayerId: string, toPlayerId: string, partyId: string, invitationToken: string) {
    const client = this.clients.get(toPlayerId);
    if (!client) {
      logger.warn(`${logPrefix} Cannot send partyGetInvitedNotif to ${toPlayerId} — not connected to AccelByte Lobby WS`);
      return false;
    }

    logger.info(`${logPrefix} Sending partyGetInvitedNotif to ${toPlayerId} from ${fromPlayerId} (party: ${partyId})`);

    this.sendMessage(client.ws, {
      type: "partyGetInvitedNotif",
      from: fromPlayerId,
      partyId: partyId,
      invitationToken: invitationToken,
    });

    return true;
  }

  /**
   * Check if a player is connected to the AccelByte Lobby WebSocket
   */
  isConnected(playerId: string): boolean {
    const client = this.clients.get(playerId);
    return !!client && client.ws.readyState === WebSocket.OPEN;
  }

  /**
   * Get count of connected AccelByte Lobby WS clients
   */
  getConnectedCount(): number {
    return this.clients.size;
  }

  // ============================================================
  // Redis Subscription (for party invites from the HTTP handler)
  // ============================================================

  private setupRedisSubscription() {
    this.redisSub.subscribe(PARTY_INVITE_CHANNEL, (message: string) => {
      try {
        const notification: RedisPartyInviteNotification = JSON.parse(message);
        logger.info(
          `${logPrefix} Redis party invite: ${notification.inviterAccountId} → ${notification.invitedAccountId}`,
        );
        this.sendPartyInviteNotification(
          notification.inviterAccountId,
          notification.invitedAccountId,
          notification.lobbyId,
          randomUUID(),
        );
      } catch (e) {
        logger.error(`${logPrefix} Error handling Redis party invite: ${e}`);
      }
    });
    logger.info(`${logPrefix} Subscribed to Redis channel: ${PARTY_INVITE_CHANNEL}`);
  }
}

// Singleton instance, set up in server.ts
export let accelByteLobbyWs: AccelByteLobbyWsService | null = null;

export function initAccelByteLobbyWs(httpServer: HttpServer): AccelByteLobbyWsService {
  accelByteLobbyWs = new AccelByteLobbyWsService(httpServer);
  return accelByteLobbyWs;
}
