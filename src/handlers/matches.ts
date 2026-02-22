import { logger, logwrapper, BE_VERBOSE } from "../config/logger";
import express, { Request, Response } from "express";
import { redisClient, RedisPlayerConnection, redisSetPlayerConnectionCosmetics, redisGetPlayer, redisGetPlayers } from "../config/redis";
import { Cosmetics, CosmeticsModel, TauntSlotsClass } from "../database/Cosmetics";
import { getEquippedCosmetics } from "../services/cosmeticsService";
import { MVSQueries } from "../interfaces/queries_types";
import ObjectID from "bson-objectid";
import { randomUUID } from "crypto";
import { MVSTime } from "../utils/date";
import env from "../env/env";
import { cancelMatchmaking, MATCH_TYPES, queueMatch } from "../services/matchmakingService";
import * as SharedTypes from "../types/shared-types";
import { HYDRA_ACCESS_TOKEN, SECRET, decodeToken } from "../middleware/auth";
import * as AuthUtils from "../utils/auth";
import * as KitchenSink from "../utils/garbagecan";

const serviceName = "Handlers.Matches";
const logPrefix: string = `[${serviceName}]:`;

export async function handleMatches_id(req: Request<{}, {}, {}, {}>, res: Response) {
  //const account = req.token;

  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;
  const hydraUsername = account.hydraUsername;
  const playerUsername = account.username;
  const wb_network_id = account.wb_network_id;
  const profile_id = account.profile_id;

  let rPlayerConnectionByID = (await redisClient.hGetAll(`connections:${aID}`)) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.warn(`${logPrefix} No Redis player connection found for player ID ${aID}, cannot set loadout.`);
  }

  //const GameplayPreferences: number = rPlayerConnectionByID.GameplayPreferences as number ?? 964;

  res.send({
    updated_at: { _hydra_unix_date: 1742265244 },
    created_at: { _hydra_unix_date: 1742265244 },
    account_id: null,
    completion_time: null,
    name: "white-green-wind-breeze-OS5dF",
    state: "open",
    access_level: "public",
    origin: "client",
    rand: 0.6975513760957894,
    winning_team: [],
    win: [],
    loss: [],
    draw: null,
    arbitration: null,
    data: {},
    server_data: {
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
      PlayerGameplayPreferences: { [aID]: Number(rPlayerConnectionByID.GameplayPreferences) ?? 964 },
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
      LockedLoadouts: { [aID]: { Character: "character_wonder_woman", Skin: "skin_wonder_woman_default" } },
      ModeString: "1v1",
      IsLobbyJoinable: true,
    },
    players: {
      all: [
        {
          account_id: aID,
          source: {},
          state: "join",
          data: {},
          identity: {
            username: hydraUsername,
            avatar: "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
            default_username: true,
            personal_data: {},
            alternate: {
              wb_network: [{ id: wb_network_id, username: playerUsername, avatar: null, email: null }],
              steam: [
                {
                  id: "76561195177950873",
                  username: playerUsername,
                  avatar: "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg",
                  email: null,
                },
              ],
            },
            usernames: [
              { auth: "hydra", username: hydraUsername },
              { auth: "steam", username: playerUsername },
              { auth: "wb_network", username: playerUsername },
            ],
            platforms: ["steam"],
            current_platform: "steam",
            is_cross_platform: false,
          },
        },
      ],
      current: [aID],
      count: 1,
    },
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
      created_at: { _hydra_unix_date: MVSTime(new Date()) },
      updated_at: { _hydra_unix_date: MVSTime(new Date()) },
      data: {},
      id: ObjectID().toHexString(),
    },
    criteria: { slug: null },
    shortcode: null,
    id: ObjectID().toHexString(),
    access: "public",
  });
}

export async function handleMatches_all_id(req: Request<{}, {}, {}, MVSQueries.Matches_all_id_QUERY>, res: Response) {
  res.send({
    matches: [],
    total_matches: 0,
    current_page: 1,
    total_pages: 1,
  });
}

export interface MATCH_MAKING_REQUEST {
  data: {
    MultiplayParams: {
      MultiplayClusterSlug: string;
      MultiplayProfileId: string;
      MultiplayRegionId: string;
      MultiplayRegionSearchId: number;
    };
    crossplay_buckets: string[];
    version: string;
  };
  game_server: {
    launch_data: {
      id: number;

      profile: string;
    };
  };
  match: string;
}

export async function handleMatches_matchmaking_1v1_retail_request(req: Request<{}, {}, MATCH_MAKING_REQUEST, {}>, res: Response) {
  // const account = req.token;

  logger.info(`${logPrefix} Received 1v1 retail matchmaking request`);

  if (BE_VERBOSE) {
    logger.info(`${logPrefix} Request is: \n`);
    KitchenSink.TryInspectRequestVerbose(req);
  }

  const account = AuthUtils.DecodeClientToken(req);

  let rPlayerConnectionByID = await redisClient.hGetAll(`connections:${account.id}`) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.error(`${logPrefix} No Redis player connection found for player ID ${account.id}, this should not happen.`);
  }

  const aID = rPlayerConnectionByID.id || account.id;
  const hydraUsername = rPlayerConnectionByID.hydraUsername || account.hydraUsername;
  const playerUsername = rPlayerConnectionByID.username || account.username;
  const wb_network_id = rPlayerConnectionByID.wb_network_id || account.wb_network_id;
  const profile_id = rPlayerConnectionByID.profile_id || account.profile_id;

  let rPlayerConnectionByIP = await redisClient.hGetAll(`connections:${rPlayerConnectionByID.current_ip}`) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByIP || !rPlayerConnectionByIP.id) {
    logger.error(`${logPrefix} No Redis player connection found for IP ${rPlayerConnectionByID.current_ip}, this should not happen.`);
  }

  let numCosmetics = await redisClient.keys(`player:${aID}:cosmetics`).then(keys => keys.length);

  if (numCosmetics === 0) {
    logger.warn(`${logPrefix} No cosmetics found in Redis for AccountId ${account.id} during matchmaking. This should not happen, as cosmetics should be cached when the player equips a cosmetic. Creating default cosmetics for this account.`);
    let rPlayerCosmetics = await getEquippedCosmetics(rPlayerConnectionByID.id) as Cosmetics;
    await redisSetPlayerConnectionCosmetics(aID, rPlayerCosmetics);
  }

  let playerLoadout = await redisGetPlayer(aID);
  if (!playerLoadout || !playerLoadout.character || !playerLoadout.skin) {
    logger.error(`${logPrefix} No Redis player loadout found for player ID ${aID}, cannot matchmake.`);
    return;
  }

  await redisClient.hSet(`connections:${aID}`, { character: playerLoadout.character, skin: playerLoadout.skin, profileIcon: playerLoadout.profileIcon });
  await redisClient.hSet(`connections:${rPlayerConnectionByID.current_ip}`, { character: playerLoadout.character, skin: playerLoadout.skin, profileIcon: playerLoadout.profileIcon });

  const data = {
    updated_at: { _hydra_unix_date: MVSTime(new Date()) },
    requester_account_id: aID,
    is_concurrent: false,
    concurrent_identifier: randomUUID(),
    created_at: { _hydra_unix_date: MVSTime(new Date()) },
    data: {
      MultiplayParams: req.body.data.MultiplayParams,
      crossplay_buckets: ["All", "PC"],
      version: env.GAME_VERSION,
      matchmaking_rating: 606.406234735998,
      player_count: 1,
      double_character_key: "character_wonder_woman",
      rp: 0,
      allowed_buckets: ["Any"],
      allowed_buckets_relaxed: ["Any"],
    },
    //server_data: null,
    server_data: {
        FoundersPack3: true,
        FoundersPack3_steam: true,
        founderpackcoolnameflag: true,
        closed_alpha_battlepass_completed: true,
    },
    criteria_slug: "1v1-retail",
    cluster: req.body.data.MultiplayParams.MultiplayClusterSlug,
    players_connection_info: {
      [aID]: {
        game_server_region_data: [{ region_id: "19c465a7-f21f-11ea-a5e3-0954f48c5682", latency: 0.04239736124873161 }],
      },
    },
    player_connections: { [aID]: [randomUUID()] },
    players: {
      [aID]: {
        updated_at: null,
        account_id: aID,
        created_at: null,
        last_login: null,
        last_inbox_read: null,
        points: null,
        data: {},
        //server_data: {},
        server_data: {
          FoundersPack3: true,
          FoundersPack3_steam: true,
          founderpackcoolnameflag: true,
          closed_alpha_battlepass_completed: true,
        },
        private_data: {},
        server_owner_data: {},
        inventory: {},
        matches: {},
        cross_match_results: {},
        notifications: {},
        aggregates: {},
        calculations: {},
        files: [],
        user_segments: [],
        random_distribution: null,
        id: profile_id,
      },
    },
    groups: [1],
    relationships: [],
    recently_played: { [aID]: [] },
    from_match: req.body.match,
    reuse_match: false,
    party_id: null,
    state: 2,
    user_rule_config: [],
    game_server: {
      unique_key: null,
      backend: "multiplay",
      launch_configs: [
        {
          profile_id: "1252922",
          fleet_id: "6edd4138-20ef-11ec-a2b7-4a5119a45304",
          region_id: "19c714ff-f21f-11ea-b144-4d87911ee195",
          backend: "multiplay",
        },
      ],
      optional_launch_config_params: {},
    },
    server_submitted: false,
    id: ObjectID().toHexString(),
  };
  res.send(data);
  await queueMatch(aID, [aID], data.from_match, data.id, MATCH_TYPES.ONE_V_ONE);
}

export async function handleMatches_matchmaking_2v2_retail_request(req: Request<{}, {}, MATCH_MAKING_REQUEST, {}>, res: Response) {
  // const account = req.token;

  logger.info("Received 2v2 retail matchmaking request");
  
  if (BE_VERBOSE) {
    logger.info(`${logPrefix} Request is: \n`)
    KitchenSink.TryInspectRequestVerbose(req);
  }

  // Should really extract this code out into a helper function since it's repeated in both 1v1 and 2v2 handlers, but
  // for now I'm just going to be lazy and copy/paste it
  const account = AuthUtils.DecodeClientToken(req);
  
  let rPlayerConnectionByID = await redisClient.hGetAll(`connections:${account.id}`) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByID || !rPlayerConnectionByID.id) {
    logger.error(`${logPrefix} No Redis player connection found for player ID ${account.id}, this should not happen.`);
  }

  const aID = rPlayerConnectionByID.id || account.id;
  const hydraUsername = rPlayerConnectionByID.hydraUsername || account.hydraUsername;
  const playerUsername = rPlayerConnectionByID.username || account.username;
  const wb_network_id = rPlayerConnectionByID.wb_network_id || account.wb_network_id;
  const profile_id = rPlayerConnectionByID.profile_id || account.profile_id;

  let rPlayerConnectionByIP = await redisClient.hGetAll(`connections:${rPlayerConnectionByID.current_ip}`) as unknown as RedisPlayerConnection;
  if (!rPlayerConnectionByIP || !rPlayerConnectionByIP.id) {
    logger.error(`${logPrefix} No Redis player connection found for IP ${rPlayerConnectionByID.current_ip}, this should not happen.`);
  }

  let numCosmetics = await redisClient.keys(`player:${aID}:cosmetics`).then(keys => keys.length);

  if (numCosmetics === 0) {
    logger.warn(`${logPrefix} No cosmetics found in Redis for AccountId ${account.id} during matchmaking. This should not happen, as cosmetics should be cached when the player equips a cosmetic. Creating default cosmetics for this account.`);
    let rPlayerCosmetics = await getEquippedCosmetics(rPlayerConnectionByID.id) as Cosmetics;
    await redisSetPlayerConnectionCosmetics(aID, rPlayerCosmetics);
  }

  let playerLoadout = await redisGetPlayer(aID);
  if (!playerLoadout || !playerLoadout.character || !playerLoadout.skin) {
    logger.error(`${logPrefix} No Redis player loadout found for player ID ${aID}, cannot matchmake.`);
    return;
  }

  await redisClient.hSet(`connections:${aID}`, { character: playerLoadout.character, skin: playerLoadout.skin, profileIcon: playerLoadout.profileIcon });
  await redisClient.hSet(`connections:${rPlayerConnectionByID.current_ip}`, { character: playerLoadout.character, skin: playerLoadout.skin, profileIcon: playerLoadout.profileIcon });

  const newMatchId = ObjectID().toHexString();

  const data = {
    id: newMatchId,
    updated_at: { _hydra_unix_date: MVSTime(new Date()) },
    requester_account_id: aID,
    is_concurrent: false,
    concurrent_identifier: randomUUID(),
    created_at: { _hydra_unix_date: MVSTime(new Date()) },
    data: {
      MultiplayParams: req.body.data.MultiplayParams,
      crossplay_buckets: ["All", "PC"],
      version: env.GAME_VERSION,
      matchmaking_rating: 724.7928014055103,
      player_count: 1,
      double_character_key: "character_TODO_SAME_CHAR_IN_SAME_TEAM",
      rp: 0,
      allowed_buckets: ["Any"],
      allowed_buckets_relaxed: ["Any"],
    },
    server_data: null,
    criteria_slug: "2v2-retail",
    cluster: req.body.data.MultiplayParams.MultiplayClusterSlug,
    players_connection_info: {
      [aID]: {
        game_server_region_data: [{ region_id: "19c465a7-f21f-11ea-a5e3-0954f48c5682", latency: 0.04791003838181496 }],
      },
    },
    player_connections: {
      [aID]: [randomUUID()],
    },
    players: {
      [aID]: {
        id: profile_id,
        updated_at: null,
        account_id: aID,
        created_at: null,
        last_login: null,
        last_inbox_read: null,
        points: null,
        data: {},
        cross_match_results: {},
        notifications: {},
        aggregates: {},
        calculations: {},
        files: [],
        random_distribution: null,
      },
    },
    groups: [1],
    relationships: [],
    recently_played: {
      [aID]: [],
    },
    from_match: req.body.match,
    reuse_match: false,
    party_id: null,
    state: 2,
    user_rule_config: [],
    game_server: {
      unique_key: null,
      backend: "multiplay",
      launch_configs: [
        {
          profile_id: "1252928",
          fleet_id: "6edd4138-20ef-11ec-a2b7-4a5119a45304",
          region_id: "19c714ff-f21f-11ea-b144-4d87911ee195",
          backend: "multiplay",
        },
      ],
      optional_launch_config_params: {},
    },
    server_submitted: false,
  };

  res.send(data);

  await queueMatch(aID, [aID], data.from_match, data.id, MATCH_TYPES.TWO_V_TWO);
}

export async function handle_cancel_matchmaking(req: Request<{ id: string }, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;

  await cancelMatchmaking(aID, req.params.id);
}
