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
  redisPublishPartyInvite,
  RedisPartyInviteNotification,
  redisSaveLobbyState,
  RedisLobbyState,
  redisGetLobbyState,
  redisSaveLobbyRedirect,
  redisSavePlayerLobby,
  redisGetPlayerLobby,
  redisPublishPlayerJoinedLobby,
  RedisPlayerJoinedLobbyNotification,
  redisGetOnlinePlayers,
  redisPublishLobbyRejoin,
  RedisLobbyRejoinNotification,
  redisGetPlayer,
  redisDeletePlayerLobby,
  redisDeleteLobbyState,
  redisUpdatePartyKeyLobby,
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
const logPrefix = `[${serviceName}]:`;

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

  // Try Redis cache first for cosmetics (avoids MongoDB timeout on Docker setups)
  const rawCosmetics = await redisClient.hGetAll(`connections:${rPlayerConnectionByID.id}:cosmetics`);
  let rPlayerCosmetics: Cosmetics;
  if (rawCosmetics && Object.keys(rawCosmetics).length > 0) {
    rPlayerCosmetics = {} as Cosmetics;
    for (const [key, value] of Object.entries(rawCosmetics)) {
      try {
        rPlayerCosmetics[key as keyof Cosmetics] = JSON.parse(value as string);
      } catch {
        rPlayerCosmetics[key as keyof Cosmetics] = value as any;
      }
    }
  } else {
    rPlayerCosmetics = (await getEquippedCosmetics(rPlayerConnectionByID.id)) as Cosmetics;
  }

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
      `[${serviceName}]: Rejected attempt to set loadout to a disabled character during loadout lock for AccountId ${aID}: ${body.Loadout.Character}`,
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
    `[${serviceName}]: Received request to create party lobby for AccountId ${aID} with character: ${loadout.Character} and IP: ${ip}`,
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
      `[${serviceName}]: Rejected attempt to set loadout to a disabled character during lobby creation for AccountId ${aID}: ${loadout.Character}`,
    );
    loadout.Character = "character_shaggy";
    loadout.Skin = "skin_shaggy_default";
  }

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

  // === REJOIN PATH: If this player is already in a multi-player lobby, return it ===
  // This handles the case where Player 1 re-calls create_party_lobby after Player 2 joined
  const myExistingLobbyId = await redisGetPlayerLobby(aID);
  if (myExistingLobbyId) {
    const myExistingLobby = await redisGetLobbyState(myExistingLobbyId);
    if (myExistingLobby && myExistingLobby.playerIds.length > 1 && myExistingLobby.playerIds.includes(aID)) {
      // Validate that ALL other players in the lobby are actually online right now
      const currentOnlinePlayers = await redisGetOnlinePlayers();
      const otherPlayersOnline = myExistingLobby.playerIds
        .filter(pid => pid !== aID)
        .every(pid => currentOnlinePlayers.includes(pid));

      if (!otherPlayersOnline) {
        // Stale lobby data — other players are not online. Clean up and fall through to normal path.
        logger.info(`${logPrefix} REJOIN SKIPPED: Lobby ${myExistingLobbyId} has offline players, cleaning up stale data`);
        // Remove this player from the stale lobby
        myExistingLobby.playerIds = myExistingLobby.playerIds.filter(pid => pid !== aID);
        if (myExistingLobby.playerIds.length === 0) {
          await redisClient.del(`lobby:${myExistingLobbyId}`);
        } else {
          await redisSaveLobbyState(myExistingLobbyId, myExistingLobby);
        }
        await redisClient.del(`player_lobby:${aID}`);
      } else {
        logger.info(`${logPrefix} REJOIN: Player ${aID} is already in multi-player lobby ${myExistingLobbyId}, returning existing lobby data`);

        // Refresh TTLs on lobby state and player_lobby keys so they don't expire mid-session
        // (both have 1hr TTL set at creation — without refresh, long sessions with many matches can silently expire them)
        await redisSaveLobbyState(myExistingLobbyId, myExistingLobby);
        for (const pid of myExistingLobby.playerIds) {
          await redisSavePlayerLobby(pid, myExistingLobbyId);
        }

        // Build the full lobby response with all current players
        const rejoinPlayers: any = {};
        const rejoinGameplayPrefs: any = {};
        const rejoinAutoParty: any = {};
        const rejoinPlatforms: any = {};
        const rejoinLoadouts: any = {};

        for (let i = 0; i < myExistingLobby.playerIds.length; i++) {
          const pid = myExistingLobby.playerIds[i];
          const pConn = (await redisClient.hGetAll(`connections:${pid}`)) as unknown as RedisPlayerConnection;
          // Use redisGetPlayer for loadout — it has the CURRENT character/skin
          // (updated by set_lock_lobby_loadout), unlike PlayerTesterModel which may be stale
          const pLoadout = await redisGetPlayer(pid);

          // Ensure cosmetics are cached in Redis for all lobby players
          // (prevents MongoDB fallback timeout during match setup)
          const pCosmetics = await getEquippedCosmetics(pid);
          await redisSetPlayerConnectionCosmetics(pid, pCosmetics);

          rejoinPlayers[pid] = {
            Account: { id: pid },
            JoinedAt: { _hydra_unix_date: MVSTime(new Date(i === 0 ? myExistingLobby.createdAt : Date.now())) },
            BotSettingSlug: "",
            LobbyPlayerIndex: i,
            CrossplayPreference: 1,
          };
          rejoinGameplayPrefs[pid] = Number(pConn?.GameplayPreferences) || 964;
          rejoinAutoParty[pid] = false;
          rejoinPlatforms[pid] = "PC";
          rejoinLoadouts[pid] = {
            Character: pLoadout?.character || "character_shaggy",
            Skin: pLoadout?.skin || "skin_shaggy_default",
          };
        }

        const rejoinResponse = {
          Teams: [
            { TeamIndex: 0, Players: rejoinPlayers, Length: myExistingLobby.playerIds.length },
            { TeamIndex: 1, Players: {}, Length: 0 },
            { TeamIndex: 2, Players: {}, Length: 0 },
            { TeamIndex: 3, Players: {}, Length: 0 },
            { TeamIndex: 4, Players: {}, Length: 0 },
          ],
          LeaderID: myExistingLobby.ownerId,
          LobbyType: 0,
          ReadyPlayers: {},
          PlayerGameplayPreferences: rejoinGameplayPrefs,
          PlayerAutoPartyPreferences: rejoinAutoParty,
          GameVersion: env.GAME_VERSION,
          HissCrc: 1167552915,
          Platforms: rejoinPlatforms,
          AllMultiplayParams: {
            "1": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252499", MultiplayRegionId: "" },
            "2": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252922", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
            "3": { MultiplayClusterSlug: "", MultiplayProfileId: "1252925", MultiplayRegionId: "" },
            "4": { MultiplayClusterSlug: "ec2-us-east-1-dokken", MultiplayProfileId: "1252928", MultiplayRegionId: "19c465a7-f21f-11ea-a5e3-0954f48c5682" },
          },
          LockedLoadouts: rejoinLoadouts,
          ModeString: myExistingLobby.playerIds.length >= 2 ? "2v2" : (myExistingLobby.mode || "1v1"),
          IsLobbyJoinable: true,
          MatchID: myExistingLobbyId,
        };

        res.send({
          body: {
            lobby: rejoinResponse,
            Cluster: "ec2-us-east-1-dokken",
          },
          metadata: null,
          return_code: 0,
        });
        return;
      }
    }
  }

  // ---- NORMAL PATH: No existing lobby to join, create a new one ----
  const lobbyMode = LOBBY_MODES.ONE_V_ONE; // Default mode, can be changed later;
  const newLobby = await createLobby(aID, lobbyMode);

  // Store lobby state in Redis so joining players can look it up by lobbyId
  const lobbyState: RedisLobbyState = {
    lobbyId: newLobby.id,
    ownerId: aID,
    ownerUsername: rPlayerConnectionByID?.username || rPlayerConnectionByID?.hydraUsername || "Unknown",
    mode: lobbyMode.toString(),
    playerIds: [aID],
    createdAt: Date.now(),
  };
  await redisSaveLobbyState(newLobby.id, lobbyState);
  await redisSavePlayerLobby(aID, newLobby.id);

  // Update party key with new lobbyId
  const myPartyKey = await redisClient.hGet(`connections:${aID}`, "party_key");
  if (myPartyKey) await redisUpdatePartyKeyLobby(myPartyKey, newLobby.id);

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
  // LobbyId from the client token is never populated — look it up from Redis instead
  const lobbyId = await redisGetPlayerLobby(aID);
  const newMode = req.body.ModeString as LOBBY_MODES;

  if (!lobbyId) {
    logger.warn(`${logPrefix} set_lobby_mode: No lobby found in Redis for player ${aID}`);
    res.status(200).json({});
    return;
  }

  await changeLobbyMode(aID, lobbyId, newMode);

  // Look up the full lobby state from Redis so we can return all players
  const lobbyState = await redisGetLobbyState(lobbyId);

  if (lobbyState && lobbyState.playerIds.length > 1) {
    // Multi-player lobby: return full lobby data so the game client updates its UI
    logger.info(`${logPrefix} set_lobby_mode: Lobby ${lobbyId} has ${lobbyState.playerIds.length} players, returning full lobby data`);

    // Build Players object and metadata for all players in the lobby
    const playersObj: any = {};
    const gameplayPrefs: any = {};
    const autoPartyPrefs: any = {};
    const platforms: any = {};
    const lockedLoadouts: any = {};

    for (let i = 0; i < lobbyState.playerIds.length; i++) {
      const pid = lobbyState.playerIds[i];
      const pConn = (await redisClient.hGetAll(`connections:${pid}`)) as unknown as RedisPlayerConnection;
      const pLoadout = await redisGetPlayer(pid);

      playersObj[pid] = {
        Account: { id: pid },
        JoinedAt: { _hydra_unix_date: MVSTime(new Date(i === 0 ? lobbyState.createdAt : Date.now())) },
        BotSettingSlug: "",
        LobbyPlayerIndex: i,
        CrossplayPreference: 1,
      };

      gameplayPrefs[pid] = Number(pConn?.GameplayPreferences) || 964;
      autoPartyPrefs[pid] = false;
      platforms[pid] = "PC";
      lockedLoadouts[pid] = {
        Character: pLoadout?.character || "character_shaggy",
        Skin: pLoadout?.skin || "skin_shaggy_default",
      };
    }

    const lobbyResponse = {
      Teams: [
        { TeamIndex: 0, Players: playersObj, Length: lobbyState.playerIds.length },
        { TeamIndex: 1, Players: {}, Length: 0 },
        { TeamIndex: 2, Players: {}, Length: 0 },
        { TeamIndex: 3, Players: {}, Length: 0 },
        { TeamIndex: 4, Players: {}, Length: 0 },
      ],
      LeaderID: lobbyState.ownerId,
      LobbyType: 0,
      ReadyPlayers: {},
      PlayerGameplayPreferences: gameplayPrefs,
      PlayerAutoPartyPreferences: autoPartyPrefs,
      GameVersion: env.GAME_VERSION,
      HissCrc: 1167552915,
      Platforms: platforms,
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
      LockedLoadouts: lockedLoadouts,
      ModeString: newMode,
      IsLobbyJoinable: true,
      MatchID: lobbyId,
    };

    res.send({
      body: {
        lobby: lobbyResponse,
        Cluster: "ec2-us-east-1-dokken",
      },
      metadata: null,
      return_code: 0,
    });
  } else {
    // Solo lobby or lobby not found: return minimal response like before
    res.send({
      body: {},
      metadata: null,
      return_code: 0,
    });
  }
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

export interface InviteToPlayerLobbyReq {
  InviteeAccountId: string;
  LobbyId: string;
  MatchID: string;
  [key: string]: any; // catch any extra fields the client sends
}

export async function handleSsc_invoke_invite_to_player_lobby(req: Request<{}, {}, {}, {}>, res: Response) {
  logger.info(`${logPrefix} Received invite_to_player_lobby request, body is:`);
  KitchenSink.TryInspectRequest(req.body);

  const account = AuthUtils.DecodeClientToken(req);
  const inviterAccountId = account.id;

  // The client may send the target player ID under various field names
  const body = req.body as any;
  const invitedAccountId =
    body.InviteeAccountID ||
    body.InviteeAccountId ||
    body.invitee_account_id ||
    body.TargetAccountId ||
    body.target_account_id ||
    body.AccountId ||
    body.account_id ||
    body.FriendAccountId ||
    body.friend_account_id ||
    "";

  const lobbyId = body.LobbyId || body.lobby_id || body.MatchID || body.match_id || "";

  logger.info(`${logPrefix} Party invite from ${inviterAccountId} to ${invitedAccountId} for lobby ${lobbyId}`);

  if (!invitedAccountId) {
    logger.warn(`${logPrefix} Could not determine invited player ID from request body. Full body: ${JSON.stringify(body)}`);
    res.send({ body: {}, metadata: null, return_code: 0 });
    return;
  }

  // Look up inviter username from Redis
  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${inviterAccountId}`)) as unknown as RedisPlayerConnection;
  const inviterUsername = rPlayerConnectionByID?.username || rPlayerConnectionByID?.hydraUsername || account.username || "Unknown";

  // Send the party invite notification via Redis → WebSocket
  // This sends MatchInviteNotification to the invited player's game client
  const notification: RedisPartyInviteNotification = {
    inviterAccountId,
    inviterUsername,
    invitedAccountId,
    matchId: lobbyId,
    lobbyId,
  };
  await redisPublishPartyInvite(notification);

  logger.info(`${logPrefix} Sent party invite notification from ${inviterAccountId} (${inviterUsername}) to ${invitedAccountId} for lobby ${lobbyId}`);

  res.send({
    body: {},
    metadata: null,
    return_code: 0,
  });
}

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

// ============================================================================
// LEAVE PLAYER LOBBY
// ============================================================================
export async function handleSsc_invoke_leave_player_lobby(req: Request<{}, {}, {}, {}>, res: Response) {
  logger.info(`${logPrefix} Received leave_player_lobby request`);
  KitchenSink.TryInspectRequest(req.body);

  const account = AuthUtils.DecodeClientToken(req);
  const leavingPlayerId = account.id;

  logger.info(`${logPrefix} Player ${leavingPlayerId} requesting to leave their lobby`);

  // 1. Find the player's current lobby
  const lobbyId = await redisGetPlayerLobby(leavingPlayerId);
  if (!lobbyId) {
    logger.warn(`${logPrefix} Player ${leavingPlayerId} is not in any lobby, nothing to leave`);
    res.send({ body: {}, metadata: null, return_code: 0 });
    return;
  }

  const lobbyState = await redisGetLobbyState(lobbyId);
  if (!lobbyState) {
    logger.warn(`${logPrefix} Lobby ${lobbyId} not found in Redis for player ${leavingPlayerId}, cleaning up stale mapping`);
    await redisDeletePlayerLobby(leavingPlayerId);
    res.send({ body: {}, metadata: null, return_code: 0 });
    return;
  }

  // 2. Collect ALL players in the lobby (including the leaver)
  const allPlayerIds = [...lobbyState.playerIds];
  logger.info(`${logPrefix} Player ${leavingPlayerId} leaving lobby ${lobbyId}. All players being kicked: ${allPlayerIds.join(", ")}`);

  // 3. Delete the entire lobby and clear player_lobby for ALL players
  await redisDeleteLobbyState(lobbyId);
  for (const pid of allPlayerIds) {
    await redisDeletePlayerLobby(pid);
  }

  // 4. Trigger rejoin for ALL players (including the leaver)
  // This force-closes everyone's WebSocket. They all reconnect to main menu
  // and call create_party_lobby, which gives each player a fresh solo lobby.
  for (const pid of allPlayerIds) {
    const rejoinNotification: RedisLobbyRejoinNotification = {
      playerId: pid,
      lobbyId,
    };
    await redisPublishLobbyRejoin(rejoinNotification);
    logger.info(`${logPrefix} Triggered lobby rejoin for player ${pid}`);
  }

  // 5. Return success
  res.send({ body: {}, metadata: null, return_code: 0 });
  logger.info(`${logPrefix} Player ${leavingPlayerId} successfully left lobby ${lobbyId}`);
}
