import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import {
  handle_ssc_set_lobby_mode,
  handleSsc_invoke_create_party_lobby,
  handleSsc_invoke_perks_get_all_pages,
  perks_set_page,
  set_lock_lobby_loadout,
  set_perks_absent,
  handle_ssc_update_player_preferences,
  handleSsc_invoke_invite_to_player_lobby,
  handleSsc_invoke_leave_player_lobby,
  // handleSsc_invoke_game_install
} from "./ssc";

export const sscRouter = express.Router();

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

sscRouter.put("/ssc/invoke/create_party_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party_lobby(req, res);
});

sscRouter.put("/ssc/invoke/set_mode_for_lobby", async (req: Request, res: Response) => {
  await handle_ssc_set_lobby_mode(req, res);
});

sscRouter.put("/ssc/invoke/update_player_preferences", async (req: Request, res: Response) => {
  await handle_ssc_update_player_preferences(req, res);
});

sscRouter.put("/ssc/invoke/create_custom_game_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_create_party_lobby(req, res);
});

sscRouter.put("/ssc/invoke/invite_to_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_invite_to_player_lobby(req, res);
});

sscRouter.put("/ssc/invoke/leave_player_lobby", async (req: Request, res: Response) => {
  await handleSsc_invoke_leave_player_lobby(req, res);
});

// Stub handlers for party-related SSC endpoints found in the game binary
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

sscRouter.put("/ssc/invoke/set_lobby_joinable", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received set_lobby_joinable request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/set_lobby_not_joinable", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received set_lobby_not_joinable request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/update_party_game_modes", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received update_party_game_modes request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});

sscRouter.put("/ssc/invoke/promote_to_lobby_leader", async (req: Request, res: Response) => {
  logger.info("[SSC.Routes]: Received promote_to_lobby_leader request");
  res.send({ body: {}, metadata: null, return_code: 0 });
});
