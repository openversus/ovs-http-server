/**
 * Custom Lobby Routes — SSC endpoints specific to the custom game lobby system.
 * Shared handlers (leave, invite, ready, loadout, rematch) are in modules/lobby/shared.routes.ts
 */
import express, { Request, Response } from "express";
import { logger } from "../../config/logger";
import * as AuthUtils from "../../utils/auth";
import { GAME_MODES_CONFIG } from "./gameModes.data";
import { type CustomLobbyMatchConfig } from "./lobby.types";
import {
  addCustomGameBot,
  updateCustomGameBotFighter,
  createCustomLobby,
  joinCustomLobby,
  kickFromLobby,
  promoteToLobbyLeader,
  resetCustomLobbySettings,
  setWorldBuffsForCustomLobby,
  switchTeamForCustomLobby,
  updateEnabledMapsForCustomLobby,
  updateGameModeForCustomLobby,
  updateHandicapsForCustomLobby,
  updateIntSettingForCustomLobby,
  updateTeamStyleForCustomLobby,
  generateLobbyCode,
  startCustomMatch,
} from "./lobby.service";

const logPrefix = "[CustomLobby.Routes]:";

export const customLobbyRouter = express.Router();

customLobbyRouter.put("/ssc/invoke/create_custom_game_lobby", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobby = await createCustomLobby(account.id);
    res.send({ body: { lobby, Cluster: "ec2-us-east-1-dokken" }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} create_custom_game_lobby error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/join_custom_game_lobby", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobby = await joinCustomLobby(req.body.HostId, account.id, req.body.IsSpectator ?? false);
    if (!lobby) {
      logger.info(`${logPrefix} join_custom_game_lobby: Lobby ${req.body.HostId} not found or not joinable`);
      res.send({ body: {}, metadata: null, return_code: 1 });
      return;
    }
    res.send({ body: { lobby, Cluster: "ec2-us-east-1-dokken", bIsJoiningCrossPlatform: false, ConnectionQuality: 0 }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} join_custom_game_lobby error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/update_team_style_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobby = await updateTeamStyleForCustomLobby(req.body.MatchID, account.id, req.body.TeamStyle);
    res.send({ body: { lobby }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} update_team_style error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

customLobbyRouter.put("/ssc/invoke/update_int_setting_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    await updateIntSettingForCustomLobby(req.body.MatchID, account.id, req.body.SettingKey as keyof CustomLobbyMatchConfig, req.body.SettingValue);
    res.send({ body: req.body, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} update_int_setting error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

customLobbyRouter.put("/ssc/invoke/set_game_mode_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const lobby = await updateGameModeForCustomLobby(req.body.MatchID, account.id, req.body.GameModeSlug as keyof typeof GAME_MODES_CONFIG);
    res.send({ body: { lobby }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} set_game_mode error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

customLobbyRouter.put("/ssc/invoke/set_enabled_maps_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const Maps = await updateEnabledMapsForCustomLobby(req.body.MatchID, account.id, req.body.MapSlugs);
    res.send({ body: { MatchID: req.body.MatchID, Maps }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} set_enabled_maps error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

customLobbyRouter.put("/ssc/invoke/set_player_handicap_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const Handicaps = await updateHandicapsForCustomLobby(req.body.MatchID, account.id, req.body.PlayerHandicap, req.body.PlayerId);
    res.send({ body: { MatchID: req.body.MatchID, Handicaps }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} set_player_handicap error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
  }
});

customLobbyRouter.put("/ssc/invoke/switch_custom_game_lobby_team", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const Player = await switchTeamForCustomLobby(req.body.MatchID, account.id, req.body.TeamIndex);
    if (!Player) { res.send({}); return; }
    res.send({ body: { MatchID: req.body.MatchID, Player, TeamIndex: req.body.TeamIndex }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} switch_team error: ${e}`);
    res.send({});
  }
});

customLobbyRouter.put("/ssc/invoke/add_custom_game_bot", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const Bot = await addCustomGameBot(req.body.MatchID, account.id, req.body.TeamIndex, {
      BotAccountID: req.body.BotAccountID, BotSettingSlug: req.body.BotSettingSlug,
      Fighter: { AssetPath: req.body.CharacterAssetPath, Slug: req.body.CharacterSlug },
      Skin: { AssetPath: req.body.SkinAssetPath, Slug: req.body.SkinSlug },
    });
    res.send({ body: { MatchID: req.body.MatchID, Bot, TeamIndex: req.body.TeamIndex }, metadata: null, return_code: Bot ? 0 : 1 });
  } catch (e) {
    logger.error(`${logPrefix} add_bot error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/update_custom_game_bot_fighter", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const Bot = await updateCustomGameBotFighter(req.body.MatchID, account.id, req.body.BotAccountID,
      { AssetPath: req.body.CharacterAssetPath, Slug: req.body.CharacterSlug },
      { AssetPath: req.body.SkinAssetPath, Slug: req.body.SkinSlug }, req.body.BotSettingSlug);
    res.send({ body: { MatchID: req.body.MatchID, Bot }, metadata: null, return_code: Bot ? 0 : 1 });
  } catch (e) {
    logger.error(`${logPrefix} update_bot_fighter error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/reset_custom_lobby_to_defaults", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const result = await resetCustomLobbySettings(req.body.MatchID, account.id);
    if (!result) { res.send({ body: { error: "Not found or not leader" }, metadata: null, return_code: 1 }); return; }
    res.send({ body: result, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} reset_to_defaults error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/promote_to_lobby_leader", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const result = await promoteToLobbyLeader(req.body.MatchID, account.id, req.body.PromoteTarget);
    res.send({ body: result, metadata: null, return_code: result ? 0 : 1 });
  } catch (e) {
    logger.error(`${logPrefix} promote_leader error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/kick_from_lobby", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const result = await kickFromLobby(req.body.MatchID, account.id, req.body.KickeeAccountID);
    res.send({ body: { MatchID: req.body.MatchID, Player: result }, metadata: null, return_code: result ? 0 : 1 });
  } catch (e) {
    logger.error(`${logPrefix} kick error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/set_world_buffs_for_custom_game", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const WorldBuffs = await setWorldBuffsForCustomLobby(req.body.MatchID, account.id, req.body.WorldBuffSlugs);
    res.send({ body: { MatchID: req.body.MatchID, WorldBuffs }, metadata: null, return_code: WorldBuffs ? 0 : 1 });
  } catch (e) {
    logger.error(`${logPrefix} set_world_buffs error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/lobby_code", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    const LobbyCode = await generateLobbyCode(req.body.LobbyId, account.id);
    res.send({ body: { LobbyCode }, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} lobby_code error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});

customLobbyRouter.put("/ssc/invoke/start_custom_match", async (req: Request, res: Response) => {
  try {
    const account = AuthUtils.DecodeClientToken(req);
    await startCustomMatch(req.body.LobbyId, account.id);
    res.send({ body: {}, metadata: null, return_code: 0 });
  } catch (e) {
    logger.error(`${logPrefix} start_custom_match error: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 1 });
  }
});
