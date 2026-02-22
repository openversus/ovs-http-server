import { logger } from "../config/logger";
import express, { Request, Response } from "express";
import {
  RedisPlayer,
  redisUpdatePlayerLoadout,
  RedisPlayerConnection,
  redisSetPlayerConnectionByID,
  redisClient,
  redisSetPlayerConnectionCosmetics,
  redisSetPlayerConnectionByIp,
} from "../config/redis";
import { Cosmetics, CosmeticsModel, TauntSlotsClass } from "../database/Cosmetics";
import { getEquippedCosmetics } from "../services/cosmeticsService";
import env from "../env/env";
import { PerkPagesModel } from "../database/PerkPages";
import { Types } from "mongoose";
import { changeLobbyMode, createLobby, LOBBY_MODES } from "../services/lobbyService";
import { MVSTime } from "../utils/date";
import * as SharedTypes from "../types/shared-types";
import { HYDRA_ACCESS_TOKEN, SECRET, decodeToken } from "../middleware/auth";
import * as AuthUtils from "../utils/auth";
import * as KitchenSink from "../utils/garbagecan";
import { PlayerTester, PlayerTesterModel } from "../database/PlayerTester";
import { AccountToken, IAccountToken } from "../types/AccountToken";
// import { IGameInstall } from "../types/shared-types";

const serviceName = "SSC.SSC";
const logPrefix: string = `[${serviceName}]:`;

interface Lock_Lobby_Loadout_REQ {
  AutoPartyPreference: boolean;
  CrossplayPreference: number;
  GameplayPreferences: number;
  HissCrc: number;
  Loadout: Loadout;
  LobbyId: string;
  LobbyTemplate: string;
  Platform: string;
  Version: string;
}

interface Loadout {
  Character: string;
  Skin: string;
}

export interface Lock_Lobby_Loadout_RES {
  body: Lock_Lobby_Loadout_RES_BODY;
  metadata: any;
  return_code: number;
}

export interface Lock_Lobby_Loadout_RES_BODY {
  AccountId: string;
  Loadout: Loadout;
  bAreAllLoadoutsLocked: boolean;
}

export async function set_lock_lobby_loadout(req: Request, res: Response<Lock_Lobby_Loadout_RES>) {
  logger.info(`${logPrefix} Received set_lock_lobby_loadout request, body is: \n`);
  KitchenSink.TryInspectRequest(req.body);

  let ip = req.ip!.replace(/^::ffff:/, "");

  let rPlayerConnectionByIP = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByIP || !rPlayerConnectionByIP.id) {
    logger.warn(`${logPrefix} No Redis player connection found for IP ${ip}, cannot set loadout.`);
  }
  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${rPlayerConnectionByIP.id}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.warn(`${logPrefix} No Redis player connection found for player ID ${rPlayerConnectionByIP.id}, cannot set loadout.`);
  }

  let rPlayerCosmetics = (await getEquippedCosmetics(rPlayerConnectionByID.id)) as Cosmetics;

  let player = await PlayerTesterModel.findOne({ ip });
  if (!player) {
    //player = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(account.id) });
    logger.warn(`${logPrefix} No player found for IP ${ip}, cannot set lobby loadout.`);

    if (rPlayerConnectionByID && rPlayerConnectionByID.id) {
      logger.info(`${logPrefix} Attempting to find player by ID ${rPlayerConnectionByID.id} as fallback.`);
    }

    player = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(rPlayerConnectionByID.id) });
    if (!player) {
      logger.info(`${logPrefix} No player found for ID ${rPlayerConnectionByID.id}, cannot set lobby loadout.`);
      return;
    }
  }

  //const aID = decodedToken.id;
  const aID = rPlayerConnectionByID.id;
  const body = req.body as Lock_Lobby_Loadout_REQ;

  if (ip === "127.0.0.1") {
    ip = env.LOCAL_PUBLIC_IP;
  }

  await redisSetPlayerConnectionCosmetics(aID, rPlayerCosmetics);

  let badChar: boolean =
    body.Loadout.Character === "character_Meeseeks" ||
    body.Loadout.Character === "Meeseeks" ||
    body.Loadout.Character === "character_supershaggy" ||
    body.Loadout.Character === "supershaggy" ||
    body.Loadout.Character === "character_c022" ||
    body.Loadout.Character === "c022" ||
    body.Loadout.Character === "character_C022" ||
    body.Loadout.Character === "C022";

  if (badChar) {
    logger.info(
      `${logPrefix} Rejected attempt to set loadout to a disabled character during loadout lock for AccountId ${aID}: ${body.Loadout.Character}`,
    );
    return;
  }

  await redisUpdatePlayerLoadout(aID, { character: body.Loadout.Character, skin: body.Loadout.Skin, ip: ip } as RedisPlayer);

  try {
    const updatedDoc = await PlayerTesterModel.findOneAndUpdate(
      { _id: new Types.ObjectId(aID) },
      {
        $set: {
          character: body.Loadout.Character,
          variant: body.Loadout.Skin,
        },
      },
    ).exec();
  }
  catch (err) {
    logger.error(`${logPrefix} Error saving character and last-used variant for player ${aID}, error: ${err}`);
  }

  res.send({
    body: {
      AccountId: aID,
      Loadout: {
        Character: body.Loadout.Character,
        Skin: body.Loadout.Skin,
      },
      bAreAllLoadoutsLocked: true,
    },
    metadata: null,
    return_code: 0,
  });
}

export interface PERKS_ABSENT_RES {
  body: PERKS_ABSENT_RES_BODY;
  metadata: any;
  return_code: number;
}

export interface PERKS_ABSENT_RES_BODY {
  message: string;
}

export async function set_perks_absent(req: Request, res: Response<PERKS_ABSENT_RES>) {
  res.send({
    body: {
      message: "Early absent report",
    },
    metadata: null,
    return_code: 2,
  });
}

export async function perks_set_page(req: Request, res: Response) {
  const { Character, Description, DisplayName, PageIndex, Perks } = req.body;

  let rawToken = req.headers[HYDRA_ACCESS_TOKEN] as string;
  let decodedToken = decodeToken(rawToken);
  const account = decodedToken;

  let account_id = "";
  try {
    //account_id = req.token.id; // Assuming this is an ObjectId or convertible
    account_id = account.id;
  }
  catch (error) {
    logger.error(`${logPrefix} Error getting account ID`, error);
    // logger.info("Req is: ", req);
    logger.info(`${logPrefix} Req.body is: `, req.body);
    logger.info(`${logPrefix} Req.token is: `, req.token);
    res.status(400).send({
      body: {
        message: "Invalid account ID",
      },
      metadata: null,
      return_code: 1,
    });
    return;
  }

  // Build the update path for this page
  const pageKey = `perk_pages.${Character}.${PageIndex}`;
  const updateValue = {
    DisplayName,
    Description,
    Perks,
  };
  // 2. Upsert the specific character/page index
  try {
    const updatedDoc = await PerkPagesModel.findOneAndUpdate(
      { account_id: new Types.ObjectId(account_id) },
      {
        $set: {
          [pageKey]: updateValue,
        },
      },
      { upsert: true, new: true },
    ).exec();
  }
  catch (err) {
    logger.error(`${logPrefix} Error saving perks for account ${account_id}, error: ${err}`);
    res.status(500).send({
      body: {
        message: "Error saving perks",
      },
      metadata: null,
      return_code: 1,
    });
    return;
  }

  res.send({
    body: {},
    metadata: null,
    return_code: 0,
  });
}

export async function handleSsc_invoke_create_party_lobby(req: Request<{}, {}, {}, {}>, res: Response) {
  const account = req.token;

  let ip = req.ip!.replace(/^::ffff:/, "");
  let player = await PlayerTesterModel.findOne({ ip });
  const aID = account?.id ?? player?.id; //player ? player.id : account.id;

  let character = "";
  let variant = "";
  let profileIcon = "";

  const playerData = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(aID) });

  if (playerData) {
    character = playerData.character;
    variant = playerData?.variant;
    profileIcon = playerData?.profile_icon || "profile_icon_default_gold";
  }

  const loadout = { Character: character, Skin: variant };
  logger.info(
    `${logPrefix} Received request to create party lobby for AccountId ${aID} with character: ${loadout.Character} and IP: ${ip}`,
  );

  if (ip === "127.0.0.1") {
    ip = env.LOCAL_PUBLIC_IP;
  }

  let badChar: boolean =
    loadout.Character === "character_Meeseeks" ||
    loadout.Character === "Meeseeks" ||
    loadout.Character === "character_supershaggy" ||
    loadout.Character === "supershaggy" ||
    loadout.Character === "character_c022" ||
    loadout.Character === "c022";

  // Default to Shaggy if a disabled character is attempted to be set
  if (badChar) {
    logger.info(
      `${logPrefix} Rejected attempt to set loadout to a disabled character during lobby creation for AccountId ${aID}: ${loadout.Character}`,
    );
    loadout.Character = "character_shaggy";
    loadout.Skin = "skin_shaggy_default";
  }

  const lobbyMode = LOBBY_MODES.ONE_V_ONE; // Default mode, can be changed later;
  //const newLobby = await createLobby(account.id, lobbyMode);
  const newLobby = await createLobby(aID, lobbyMode);

  let rPlayerConnectionByIP = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByIP || !rPlayerConnectionByIP.id) {
    logger.warn(`${logPrefix} No Redis player connection found for IP ${ip}, cannot set loadout.`);
  }
  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${rPlayerConnectionByIP.id}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.warn(`${logPrefix} No Redis player connection found for player ID ${rPlayerConnectionByIP.id}, cannot set loadout.`);
  }

  let rPlayerCosmetics = (await getEquippedCosmetics(rPlayerConnectionByID.id)) as Cosmetics;
  await redisSetPlayerConnectionCosmetics(aID, rPlayerCosmetics);

  await redisUpdatePlayerLoadout(aID, { character: character, skin: variant, ip: ip, profileIcon: profileIcon } as RedisPlayer);
  //const GameplayPreferences: number = rPlayerConnectionByID.GameplayPreferences as number ?? 964;
  res.send({
    body: {
      lobby: {
        Teams: [
          {
            TeamIndex: 0,
            Players: {
              [aID]: {
                Account: { id: aID },
                JoinedAt: { _hydra_unix_date: MVSTime(new Date()) },
                BotSettingSlug: "",
                LobbyPlayerIndex: 0,
                CrossplayPreference: 1,
              },
            },
            Length: 1,
          },
          { TeamIndex: 1, Players: {}, Length: 0 },
          { TeamIndex: 2, Players: {}, Length: 0 },
          { TeamIndex: 3, Players: {}, Length: 0 },
          { TeamIndex: 4, Players: {}, Length: 0 },
        ],
        LeaderID: aID,
        LobbyType: 0,
        ReadyPlayers: {},
        //PlayerGameplayPreferences: { [aID]: 544 },
        PlayerGameplayPreferences: { [aID]: (rPlayerConnectionByID.GameplayPreferences as number) ?? 964 },
        PlayerAutoPartyPreferences: { [aID]: false },
        GameVersion: env.GAME_VERSION,
        HissCrc: 1167552915,
        Platforms: { [aID]: "PC" },
        AllMultiplayParams: {
          "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
          "2": {
            MultiplayClusterSlug: "ec2-us-east-1-dokken",
            MultiplayProfileId: "1252922",
            MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
          },
          "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
          "4": {
            MultiplayClusterSlug: "ec2-us-east-1-dokken",
            MultiplayProfileId: "1252928",
            MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
          },
        },
        LockedLoadouts: { [aID]: { Character: loadout.Character, Skin: loadout.Skin } },
        ModeString: lobbyMode.toString(),
        IsLobbyJoinable: true,
        MatchID: newLobby.id,
      },
      Cluster: "ec2-us-east-1-dokken",
    },
    metadata: null,
    return_code: 0,
  });
}

export async function handleSsc_invoke_perks_get_all_pages(req: Request<{}, {}, {}, {}>, res: Response) {
  const accountId = req.token.id;

  PerkPagesModel.findOne({ account_id: new Types.ObjectId(accountId) })
    .select("perk_pages -_id")
    .lean()
    .exec()
    .then((doc) => {
      res.send({
        body: {
          perk_pages: doc?.perk_pages || {},
        },
        metadata: null,
        return_code: 0,
      });
    })
    .catch((e) => {
      logger.error(`${logPrefix} Error fetching perk pages for account ${accountId}, error: ${e}`);
      res.send({
        body: {
          perk_pages: {},
        },
        metadata: null,
        return_code: 0,
      });
    });
}

export interface SET_LOBBY_MODE_REQ {
  AutoPartyPreference: boolean;
  CrossplayPreference: number;
  GameplayPreferences: number;
  HissCrc: number;
  LobbyId: string;
  LobbyTemplate: string;
  ModeString: string;
  Platform: string;
  Version: string;
}

export async function handle_ssc_set_lobby_mode(req: Request<{}, {}, SET_LOBBY_MODE_REQ, {}>, res: Response) {
  logger.info(`${logPrefix} Received set_lobby_mode request:\n`);
  KitchenSink.TryInspectRequestVerbose(req);

  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;
  await changeLobbyMode(aID, req.body.LobbyId, req.body.ModeString as LOBBY_MODES);
  res.send({
    body: {},
    metadata: null,
    return_code: 0,
  });
}

export async function handle_ssc_update_player_preferences(req: Request<{}, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  let ip = account.current_ip;

  logger.info("Received update player prefs request, headers:\n")
  if (req.headers)
  {
    KitchenSink.TryInspectVerbose(req.headers);
  }

  logger.info("Received update player prefs request, body:\n")
  if (req.body)
  {
    KitchenSink.TryInspectVerbose(req.body);
  }

  let updatedPrefs = req.body as IUpdatePlayerPrefs;
  let updateGameplayPrefs = updatedPrefs.GameplayPreferences as number;
  try {
    await PlayerTesterModel.findOneAndUpdate( { ip }, { GameplayPreferences: (updateGameplayPrefs as number) } , { upsert: true, new: true } );
    logger.info(`${logPrefix} Updated GameplayPreferences to ${updateGameplayPrefs} for player with IP ${ip}`);  
  }
  catch (error) {
    logger.error(`${logPrefix} Error updating GameplayPreferences for player with IP ${ip}, error: ${error}`);
  }

  try {
    let rPlayerConnectionByIP = (await redisClient.hGetAll(`connections:${ip}`)) as unknown as RedisPlayerConnection;
    rPlayerConnectionByIP.GameplayPreferences = (updateGameplayPrefs as number) ?? 964;
    await redisSetPlayerConnectionByIp(ip, rPlayerConnectionByIP);
    let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${rPlayerConnectionByIP.id}`)) as unknown as RedisPlayerConnection;
    rPlayerConnectionByID.GameplayPreferences = (updateGameplayPrefs as number) ?? 964;
    await redisSetPlayerConnectionByID(rPlayerConnectionByID.id, rPlayerConnectionByID);

    logger.info(`${logPrefix} Updated GameplayPreferences in Redis to ${updateGameplayPrefs} for player with IP ${ip} and ID ${rPlayerConnectionByIP.id}`);
  }
  catch (error) {
    logger.error(`${logPrefix} Error updating GameplayPreferences in Redis for player with IP ${ip}, error: ${error}`);
  }

  res.status(200).send({ body: {}, metadata: null, return_code: 0 });
}

// export async function handleSsc_invoke_game_install(req: Request<{}, {}, {}, {}>, res: Response) {
//   logger.info(`${logPrefix} Received invoke_game_install request:\n`);
//   KitchenSink.TryInspectRequestVerbose(req);

//   const account = AuthUtils.DecodeClientToken(req);
//   const aID = account.id;
//   let ip = account.current_ip;

//   try {
//     let gameInstall: IGameInstall = req.body as IGameInstall;
//     let mongoPlayer = await PlayerTesterModel.findOne({ ip });

//     if (mongoPlayer)
//     {
//       mongoPlayer.game_install = gameInstall;
//       mongoPlayer.platform_id = gameInstall.account_platform_id;
//       mongoPlayer.platform_name = gameInstall.platform_name;
//       mongoPlayer.hydra_public_id = gameInstall.hydra_public_id;
//       await mongoPlayer.save();
//       logger.info(`${logPrefix} Updated game install info for Player ID ${aID} with IP ${ip} and name ${mongoPlayer.name ?? gameInstall.user_name ?? "Unknown"}`);
//     }
//   }
//   catch (error) {
//     logger.error(`${logPrefix} Error parsing game install data from request body for account ${aID}, error: ${error}`);
//   }
  
//   res.status(200).send({ body: {}, metadata: null, return_code: 0 });
// }

export interface IUpdatePlayerPrefs {
  AutoPartyPreference: boolean;
  /**
   *
   * 1
   *
   */
  CrossplayPreference: number;
  /**
   *
   * 544
   *
   */
  GameplayPreferences: number;
  /**
   *
   * 1914377025
   *
   */
  HissCrc: number;
  /**
   *
   * 67ead64959521e4ff6c1eabb
   *
   */
  LobbyId: string;
  /**
   *
   * party_lobby
   *
   */
  LobbyTemplate: string;
  /**
   *
   * 67ead64959521e4ff6c1eabb
   *
   */
  MatchID: string;
  /**
   *
   * PC
   *
   */
  Platform: string;
  /**
   *
   * CLIENT:2FAE7-Retail DATA:4CF442B2 PERKS:1
   *
   */
  Version: string;
}
