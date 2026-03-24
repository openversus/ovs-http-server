/**
 * Shared Lobby Routes — handles SSC endpoints that serve BOTH custom AND party lobbies.
 * These handlers detect the lobby type and route accordingly, falling through
 * to old handlers in ssc/routes.ts when not applicable.
 */
import express, { Request, Response } from "express";
import { logger } from "../../config/logger";
import { redisClient } from "../../config/redis";
import * as AuthUtils from "../../utils/auth";
import {
  invitePlayerToLobby,
  getLobby,
  getLobbyIdFromCode,
  getPlayerCustomLobbyId,
  leaveLobby as leaveSscLobby,
  updatePlayerLoadout,
  setPlayerReady,
  handleSscRematchAccept,
  handleSscRematchDecline,
} from "../customLobby/lobby.service";

const logPrefix = "[Lobby.Shared]:";

export const sharedLobbyRouter = express.Router();

// ─── create_party_lobby: if player is in a custom lobby, return it instead ───
sharedLobbyRouter.put("/ssc/invoke/create_party_lobby", async (req: Request, res: Response, next) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const customLobbyId = await getPlayerCustomLobbyId(account.id);
    if (customLobbyId) {
      const lobby = await getLobby(customLobbyId);
      if (lobby) {
        logger.info(`${logPrefix} create_party_lobby: Player ${account.id} is in custom lobby ${customLobbyId}, returning it`);
        res.send({
          body: { lobby, Cluster: "ec2-us-east-1-dokken" },
          metadata: null,
          return_code: 0,
        });
        return;
      }
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} create_party_lobby intercept error: ${e}`);
    next();
  }
});

// ─── leave_player_lobby: handles custom lobby, party lobby, or fallthrough ───
sharedLobbyRouter.put("/ssc/invoke/leave_player_lobby", async (req: Request, res: Response, next) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobbyId = req.body.LobbyId;

    // Check if it's a custom lobby (by key or player tracking)
    const customLobbyExists = await redisClient.exists(`custom_lobby_ssc:${lobbyId}`);
    if (customLobbyExists) {
      logger.info(`${logPrefix} leave_player_lobby: Player ${account.id} leaving custom lobby ${lobbyId}`);
      const newLobby = await leaveSscLobby(lobbyId, account.id, true);
      res.send({ body: { lobby: newLobby }, metadata: null, return_code: 0 });
      return;
    }
    const customLobbyId = await getPlayerCustomLobbyId(account.id);
    if (customLobbyId) {
      logger.info(`${logPrefix} leave_player_lobby: Player ${account.id} leaving custom lobby ${customLobbyId} (sent LobbyId: ${lobbyId})`);
      const newLobby = await leaveSscLobby(customLobbyId, account.id, true);
      res.send({ body: { lobby: newLobby }, metadata: null, return_code: 0 });
      return;
    }

    // Check if it's a party lobby
    const partyLobbyState = await redisClient.get(`lobby:${lobbyId}`);
    if (partyLobbyState) {
      try {
        const parsed = JSON.parse(partyLobbyState);
        if (parsed.playerIds && parsed.playerIds.includes(account.id)) {
          logger.info(`${logPrefix} leave_player_lobby: Player ${account.id} leaving party lobby ${lobbyId}`);

          parsed.playerIds = parsed.playerIds.filter((pid: string) => pid !== account.id);
          if (parsed.playerIds.length === 0) {
            await redisClient.del(`lobby:${lobbyId}`);
          } else {
            await redisClient.set(`lobby:${lobbyId}`, JSON.stringify(parsed), { EX: 3600 });
          }

          await redisClient.del(`player_lobby:${account.id}`);
          await redisClient.del(`party_ready:${lobbyId}`);

          // Notify remaining players
          if (parsed.playerIds.length > 0) {
            const msg = {
              targetPlayerIds: parsed.playerIds,
              excludePlayerIds: [],
              notification: {
                data: {
                  MatchID: lobbyId,
                  template_id: "PlayerLeftLobby",
                  Player: {
                    Account: { id: account.id },
                    LobbyPlayerIndex: 0,
                    JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
                    BotSettingSlug: "",
                    CrossplayPreference: 1,
                  },
                  ReadyPlayers: {},
                  NewLeader: parsed.playerIds[0],
                },
                payload: { custom_notification: "realtime", match: { id: lobbyId } },
                cmd: "update",
                header: "",
              },
            };
            await redisClient.publish("custom_lobby:notification", JSON.stringify(msg));
            logger.info(`${logPrefix} leave_player_lobby: Sent PlayerLeftLobby to remaining players in ${lobbyId}`);
          }

          // Return new solo lobby
          const crypto = require("crypto");
          const newMatchId = crypto.randomBytes(12).toString("hex");
          const connData = await redisClient.hGetAll(`connections:${account.id}`);
          const character = connData?.character || "character_shaggy";
          const skin = connData?.skin || "skin_shaggy_default";
          const gameplayPrefs = Number(connData?.GameplayPreferences) || 964;

          const soloLobby = {
            Teams: [
              { TeamIndex: 0, Players: { [account.id]: { Account: { id: account.id }, JoinedAt: { _hydra_unix_date: Math.floor(Date.now() / 1000) }, BotSettingSlug: "", LobbyPlayerIndex: 0, CrossplayPreference: 1 } }, Length: 1 },
              { TeamIndex: 1, Players: {}, Length: 0 },
              { TeamIndex: 2, Players: {}, Length: 0 },
              { TeamIndex: 3, Players: {}, Length: 0 },
              { TeamIndex: 4, Players: {}, Length: 0 },
            ],
            LeaderID: account.id, LobbyType: 0, ReadyPlayers: {},
            PlayerGameplayPreferences: { [account.id]: gameplayPrefs },
            PlayerAutoPartyPreferences: { [account.id]: false },
            GameVersion: "local", HissCrc: 1167552915,
            Platforms: { [account.id]: "PC" },
            AllMultiplayParams: {
              "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
              "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
              "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
              "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
            },
            LockedLoadouts: { [account.id]: { Character: character, Skin: skin } },
            ModeString: "1v1", IsLobbyJoinable: true, MatchID: newMatchId,
          };

          await redisClient.set(`lobby:${newMatchId}`, JSON.stringify({
            lobbyId: newMatchId, ownerId: account.id,
            ownerUsername: connData?.username || connData?.hydraUsername || "Unknown",
            mode: "1v1", playerIds: [account.id], createdAt: Date.now(),
          }), { EX: 3600 });
          await redisClient.set(`player_lobby:${account.id}`, newMatchId, { EX: 3600 });

          res.send({ body: { lobby: soloLobby, Cluster: "ec2-us-east-1-dokken" }, metadata: null, return_code: 0 });
          return;
        }
      } catch {}
    }

    next();
  } catch (e) {
    logger.error(`${logPrefix} leave_player_lobby error: ${e}`);
    next();
  }
});

// ─── invite_to_player_lobby: detects custom lobby vs party lobby ─────────────
sharedLobbyRouter.put("/ssc/invoke/invite_to_player_lobby", async (req: Request, res: Response, next) => {
  try {
    const lobbyId = req.body.LobbyId || req.body.MatchID;
    const customLobbyExists = await redisClient.exists(`custom_lobby_ssc:${lobbyId}`);
    if (customLobbyExists) {
      const account = AuthUtils.DecodeClientToken(req);
      const inviteeId = req.body.InviteeAccountID;
      const isSpectator = req.body.IsSpectator ?? false;
      logger.info(`${logPrefix} Custom lobby invite from ${account.id} to ${inviteeId} for lobby ${lobbyId}`);
      await invitePlayerToLobby(lobbyId, account.id, inviteeId, isSpectator);
      res.send({ body: { MatchID: lobbyId, IsSpectator: isSpectator }, metadata: null, return_code: 0 });
      return;
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} invite_to_player_lobby error: ${e}`);
    next();
  }
});

// ─── lock_lobby_loadout: detects custom lobby ────────────────────────────────
sharedLobbyRouter.put("/ssc/invoke/lock_lobby_loadout", async (req: Request, res: Response, next) => {
  try {
    const lobbyId = req.body.LobbyId;
    const customLobbyExists = await redisClient.exists(`custom_lobby_ssc:${lobbyId}`);
    if (customLobbyExists) {
      const account = AuthUtils.DecodeClientToken(req);
      const character = req.body.Loadout?.Character;
      const skin = req.body.Loadout?.Skin;
      logger.info(`${logPrefix} lock_lobby_loadout: ${account.id} → ${character} / ${skin} in custom lobby ${lobbyId}`);
      await updatePlayerLoadout(account.id, lobbyId, character, skin);
      res.send({
        body: { AccountId: account.id, Loadout: { Character: character, Skin: skin }, bAreAllLoadoutsLocked: true },
        metadata: null, return_code: 0,
      });
      return;
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} lock_lobby_loadout error: ${e}`);
    next();
  }
});

// ─── set_ready_for_lobby: handles custom + party lobbies ─────────────────────
sharedLobbyRouter.put("/ssc/invoke/set_ready_for_lobby", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const matchId = req.body.MatchID || req.body.LobbyId;
    const ready = req.body.Ready;

    // Custom lobby
    const customExists = await redisClient.exists(`custom_lobby_ssc:${matchId}`);
    if (customExists) {
      const result = await setPlayerReady(matchId, account.id, ready);
      res.send({ body: result, metadata: null, return_code: 0 });
      return;
    }

    // Party lobby ready tracking
    const readyKey = `party_ready:${matchId}`;
    if (ready) {
      await redisClient.sAdd(readyKey, account.id);
    } else {
      await redisClient.del(readyKey);
    }
    await redisClient.expire(readyKey, 3600);

    const lobbyState = await redisClient.get(`lobby:${matchId}`);
    let totalPlayers = 1;
    if (lobbyState) {
      try {
        const parsed = JSON.parse(lobbyState);
        totalPlayers = parsed.playerIds?.length || 1;
      } catch {}
    }

    const readyCount = await redisClient.sCard(readyKey);
    const allReady = readyCount >= totalPlayers && totalPlayers > 0;

    logger.info(`${logPrefix} set_ready_for_lobby: Player ${account.id} ready=${ready} in party lobby ${matchId} (${readyCount}/${totalPlayers}, allReady=${allReady})`);

    // Broadcast ready state to other players
    if (lobbyState) {
      try {
        const parsed = JSON.parse(lobbyState);
        const playerIds = parsed.playerIds || [];

        const msg = {
          targetPlayerIds: playerIds,
          excludePlayerIds: [account.id],
          notification: {
            data: {
              template_id: "PlayerReadyForLobby",
              MatchID: matchId, PlayerID: account.id, Ready: ready,
              bAllPlayersReady: false,
            },
            payload: { match: { id: matchId }, custom_notification: "realtime" },
            header: "", cmd: "update",
          },
        };
        await redisClient.publish("custom_lobby:notification", JSON.stringify(msg));

        if (allReady) {
          const allReadyMsg = {
            targetPlayerIds: playerIds,
            excludePlayerIds: [account.id],
            notification: {
              data: {
                template_id: "PlayerReadyForLobby",
                MatchID: matchId, PlayerID: account.id, Ready: true,
                bAllPlayersReady: true,
              },
              payload: { match: { id: matchId }, custom_notification: "realtime" },
              header: "", cmd: "update",
            },
          };
          await redisClient.publish("custom_lobby:notification", JSON.stringify(allReadyMsg));
        }
      } catch {}
    }

    res.send({
      body: { MatchID: matchId, PlayerID: account.id, Ready: ready, bAllPlayersReady: allReady },
      metadata: null, return_code: 0,
    });
  } catch (e) {
    logger.error(`${logPrefix} set_ready_for_lobby error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

// ─── rematch_accept: detects SSC custom lobby ────────────────────────────────
sharedLobbyRouter.put("/ssc/invoke/rematch_accept", async (req: Request, res: Response, next) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      const lobbyId = await redisClient.get(`ssc_custom_lobby_player:${playerId}`);
      if (lobbyId) {
        logger.info(`${logPrefix} SSC rematch accept from ${playerId}`);
        await handleSscRematchAccept(playerId);
        res.send({ body: [], metadata: null, return_code: 0 });
        return;
      }
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} rematch_accept error: ${e}`);
    next();
  }
});

// ─── rematch_decline: detects SSC custom lobby ───────────────────────────────
sharedLobbyRouter.put("/ssc/invoke/rematch_decline", async (req: Request, res: Response, next) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const playerId = account?.id;
    if (playerId) {
      const lobbyId = await redisClient.get(`ssc_custom_lobby_player:${playerId}`);
      if (lobbyId) {
        logger.info(`${logPrefix} SSC rematch decline from ${playerId}`);
        const playerIds = await handleSscRematchDecline(playerId);
        if (playerIds.length > 0) {
          await redisClient.publish("custom_lobby_rematch_decline", JSON.stringify({ playerIds }));
        }
        res.send({ body: [], metadata: null, return_code: 0 });
        return;
      }
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} rematch_decline error: ${e}`);
    next();
  }
});

// ─── Lobby code resolution: GET /matches/{code} ─────────────────────────────
sharedLobbyRouter.get("/matches/:id", async (req: Request, res: Response, next) => {
  try {
    const idOrCode = req.params.id;
    if (idOrCode.length <= 10) {
      const lobbyId = await getLobbyIdFromCode(idOrCode.toUpperCase());
      if (lobbyId) {
        const lobby = await getLobby(lobbyId);
        if (lobby) {
          logger.info(`${logPrefix} Lobby code ${idOrCode} resolved to ${lobbyId}`);
          res.send({
            updated_at: new Date(), created_at: new Date(),
            account_id: null, completion_time: null,
            name: "white-green-wind-breeze-OS5dF", state: "open",
            access_level: "public", origin: "client", rand: Math.random(),
            winning_team: [], win: [], loss: [], draw: null, arbitration: null,
            data: {}, server_data: lobby, players: {}, matchmaking: null,
            cluster: "ec2-us-east-1-dokken", last_warning_time: null,
            template: {
              type: "async", name: "custom_game_lobby", slug: "custom_game_lobby",
              min_players: 1, max_players: 8,
              game_server_integration_enabled: false, game_server_config: null,
              created_at: new Date(), updated_at: new Date(), data: {}, id: "",
            },
            criteria: { slug: null }, shortcode: null, id: lobbyId, access: "public",
          });
          return;
        }
      }
      logger.info(`${logPrefix} Lobby code ${idOrCode} not found`);
    }
    next();
  } catch (e) {
    logger.error(`${logPrefix} matches/:id error: ${e}`);
    next();
  }
});
