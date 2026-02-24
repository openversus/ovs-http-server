import { logger, logwrapper, BE_VERBOSE } from "../config/logger";
import express, { Request, Response } from "express";
import {
  redisClient,
  RedisPlayerConnection,
  redisSetPlayerConnectionCosmetics,
  redisGetPlayer,
  redisGetPlayers,
  redisGetLobbyState,
  redisSaveLobbyState,
  redisPublishPlayerJoinedLobby,
  RedisPlayerJoinedLobbyNotification,
  redisGetLobbyRedirect,
  redisGetPlayerLobby,
} from "../config/redis";
import { Cosmetics, CosmeticsModel, TauntSlotsClass } from "../database/Cosmetics";
import { getEquippedCosmetics } from "../services/cosmeticsService";
import { MVSQueries } from "../interfaces/queries_types";
import ObjectID from "bson-objectid";
import { randomUUID } from "crypto";
import { MVSTime } from "../utils/date";
import env from "../env/env";
import { cancelMatchmaking, cancelMatchmakingForAll, MATCH_TYPES, queueMatch } from "../services/matchmakingService";
import * as SharedTypes from "../types/shared-types";
import { HYDRA_ACCESS_TOKEN, SECRET, decodeToken } from "../middleware/auth";
import * as AuthUtils from "../utils/auth";
import * as KitchenSink from "../utils/garbagecan";

const serviceName = "Handlers.Matches";
const logPrefix = `[${serviceName}]:`;

export async function handleMatches_id(req: Request<{}, {}, {}, {}>, res: Response) {
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

  // Check if this is a join to an existing lobby
  let matchId = (req as any).params?.id;

  // Check for lobby redirect (player was force-joined into another lobby)
  const redirectedLobbyId = matchId ? await redisGetLobbyRedirect(matchId) : null;
  if (redirectedLobbyId) {
    logger.info(`${logPrefix} Lobby redirect: ${matchId} -> ${redirectedLobbyId} for player ${aID}`);
    matchId = redirectedLobbyId;
  }

  // Also check if the player has been assigned to a different lobby
  const playerLobbyId = await redisGetPlayerLobby(aID);
  if (playerLobbyId && playerLobbyId !== matchId) {
    const playerLobby = await redisGetLobbyState(playerLobbyId);
    if (playerLobby && playerLobby.playerIds.includes(aID) && playerLobby.ownerId !== aID) {
      logger.info(`${logPrefix} Player ${aID} was force-joined into lobby ${playerLobbyId}, redirecting from ${matchId}`);
      matchId = playerLobbyId;
    }
  }

  const existingLobby = matchId ? await redisGetLobbyState(matchId) : null;

  if (existingLobby && existingLobby.ownerId !== aID) {
    // This is a JOIN — player is accepting an invite to an existing lobby
    logger.info(`${logPrefix} Player ${aID} (${playerUsername}) joining existing lobby ${matchId} owned by ${existingLobby.ownerId}`);

    // Add joining player to lobby state
    if (!existingLobby.playerIds.includes(aID)) {
      existingLobby.playerIds.push(aID);
      await redisSaveLobbyState(matchId, existingLobby);
    }

    // Get owner's connection data for the response
    const ownerConn = (await redisClient.hGetAll(`connections:${existingLobby.ownerId}`)) as unknown as RedisPlayerConnection;
    const ownerLoadout = await redisGetPlayer(existingLobby.ownerId);

    // Build Teams with both players
    const teams: any[] = [
      {
        TeamIndex: 0,
        Players: {
          [existingLobby.ownerId]: {
            Account: { id: existingLobby.ownerId },
            JoinedAt: { _hydra_unix_date: MVSTime(new Date(existingLobby.createdAt)) },
            BotSettingSlug: "",
            LobbyPlayerIndex: 0,
            CrossplayPreference: 1,
          },
          [aID]: {
            Account: { id: aID },
            JoinedAt: { _hydra_unix_date: MVSTime(new Date()) },
            BotSettingSlug: "",
            LobbyPlayerIndex: 1,
            CrossplayPreference: 1,
          },
        },
        Length: 2,
      },
      { TeamIndex: 1, Players: {}, Length: 0 },
      { TeamIndex: 2, Players: {}, Length: 0 },
      { TeamIndex: 3, Players: {}, Length: 0 },
      { TeamIndex: 4, Players: {}, Length: 0 },
    ];

    const ownerCharacter = ownerLoadout?.character || "character_shaggy";
    const ownerSkin = ownerLoadout?.skin || "skin_shaggy_default";
    const joinerCharacter = rPlayerConnectionByID?.character || "character_shaggy";
    const joinerSkin = rPlayerConnectionByID?.skin || "skin_shaggy_default";

    // Notify the lobby owner that someone joined
    const joinNotification: RedisPlayerJoinedLobbyNotification = {
      lobbyId: matchId,
      ownerId: existingLobby.ownerId,
      joinedPlayerId: aID,
      joinedPlayerUsername: playerUsername || hydraUsername || "Unknown",
      allPlayerIds: existingLobby.playerIds,
      mode: existingLobby.mode || "1v1",
    };
    await redisPublishPlayerJoinedLobby(joinNotification);

    res.send({
      updated_at: { _hydra_unix_date: MVSTime(new Date()) },
      created_at: { _hydra_unix_date: MVSTime(new Date(existingLobby.createdAt)) },
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
      server_data: {
        Teams: teams,
        LeaderID: existingLobby.ownerId,
        LobbyType: 0,
        ReadyPlayers: {},
        PlayerGameplayPreferences: {
          [existingLobby.ownerId]: Number(ownerConn?.GameplayPreferences) || 964,
          [aID]: Number(rPlayerConnectionByID?.GameplayPreferences) || 964,
        },
        PlayerAutoPartyPreferences: { [existingLobby.ownerId]: false, [aID]: false },
        GameVersion: env.GAME_VERSION,
        HissCrc: 1167552915,
        Platforms: { [existingLobby.ownerId]: "PC", [aID]: "PC" },
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
        LockedLoadouts: {
          [existingLobby.ownerId]: { Character: ownerCharacter, Skin: ownerSkin },
          [aID]: { Character: joinerCharacter, Skin: joinerSkin },
        },
        ModeString: existingLobby.mode || "1v1",
        IsLobbyJoinable: true,
      },
      players: {
        all: [
          {
            account_id: existingLobby.ownerId,
            source: {},
            state: "join",
            data: {},
            identity: {
              username: ownerConn?.hydraUsername || existingLobby.ownerUsername,
              avatar: "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
              default_username: true,
              personal_data: {},
              alternate: {
                wb_network: [{ id: ownerConn?.wb_network_id || existingLobby.ownerId, username: ownerConn?.username || existingLobby.ownerUsername, avatar: null, email: null }],
                steam: [{ id: "76561195177950873", username: ownerConn?.username || existingLobby.ownerUsername, avatar: "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg", email: null }],
              },
              usernames: [
                { auth: "hydra", username: ownerConn?.hydraUsername || existingLobby.ownerUsername },
                { auth: "steam", username: ownerConn?.username || existingLobby.ownerUsername },
                { auth: "wb_network", username: ownerConn?.username || existingLobby.ownerUsername },
              ],
              platforms: ["steam"],
              current_platform: "steam",
              is_cross_platform: false,
            },
          },
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
                steam: [{ id: "76561195177950873", username: playerUsername, avatar: "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg", email: null }],
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
        current: [existingLobby.ownerId, aID],
        count: 2,
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
      id: matchId,
      access: "public",
    });
    return;
  }

  // Default: new lobby / solo lobby (original behavior)
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

  // If the player's lobby has 2+ players, force 2v2 instead of 1v1
  const preCheckAccount = AuthUtils.DecodeClientToken(req);
  const preCheckLobbyId = await redisGetPlayerLobby(preCheckAccount.id);
  if (preCheckLobbyId) {
    const preCheckLobby = await redisGetLobbyState(preCheckLobbyId);
    if (preCheckLobby && preCheckLobby.playerIds.length >= 2) {
      logger.info(`${logPrefix} Lobby ${preCheckLobbyId} has ${preCheckLobby.playerIds.length} players, redirecting 1v1 request to 2v2 handler`);
      return handleMatches_matchmaking_2v2_retail_request(req, res);
    }
  }

  if (BE_VERBOSE) {
    logger.info(`${logPrefix} Request is: \n`);
    KitchenSink.TryInspectRequestVerbose(req);
  }

  const account = preCheckAccount;

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

  // Look up the lobby to include ALL players in matchmaking, not just the requester
  const lobbyId = await redisGetPlayerLobby(aID);
  const lobbyState = lobbyId ? await redisGetLobbyState(lobbyId) : null;
  const allPlayerIds = lobbyState ? lobbyState.playerIds : [aID];

  logger.info(`${logPrefix} 2v2 matchmaking: lobby ${lobbyId}, all players: ${allPlayerIds.join(", ")}`);

  // Verify all lobby players are still connected (have active Redis connection data)
  // Prevents queuing with a disconnected/crashed teammate
  for (const pid of allPlayerIds) {
    if (pid === aID) continue; // Requester is obviously connected
    const pConn = await redisClient.hGetAll(`connections:${pid}`) as unknown as RedisPlayerConnection;
    if (!pConn || !pConn.id) {
      logger.warn(`${logPrefix} 2v2 matchmaking: Player ${pid} has no active connection — cannot queue with disconnected teammate`);
      res.status(200).json({ error: "Not all party members are connected" });
      return;
    }
  }

  // Ensure all lobby players' loadouts are cached in Redis
  for (const pid of allPlayerIds) {
    if (pid === aID) continue; // Already done above
    const pLoadout = await redisGetPlayer(pid);
    if (pLoadout) {
      const pConn = await redisClient.hGetAll(`connections:${pid}`) as unknown as RedisPlayerConnection;
      if (pConn?.current_ip) {
        await redisClient.hSet(`connections:${pid}`, { character: pLoadout.character, skin: pLoadout.skin, profileIcon: pLoadout.profileIcon });
        await redisClient.hSet(`connections:${pConn.current_ip}`, { character: pLoadout.character, skin: pLoadout.skin, profileIcon: pLoadout.profileIcon });
      }
    }
  }

  const newMatchId = ObjectID().toHexString();

  // Build players_connection_info, player_connections, players, and recently_played for ALL lobby players
  const playersConnectionInfo: any = {};
  const playerConnections: any = {};
  const playersObj: any = {};
  const recentlyPlayed: any = {};

  for (const pid of allPlayerIds) {
    const pConn = await redisClient.hGetAll(`connections:${pid}`) as unknown as RedisPlayerConnection;
    playersConnectionInfo[pid] = {
      game_server_region_data: [{ region_id: "19c465a7-f21f-11ea-a5e3-0954f48c5682", latency: 0.04791003838181496 }],
    };
    playerConnections[pid] = [randomUUID()];
    playersObj[pid] = {
      id: pConn?.profile_id || profile_id,
      updated_at: null,
      account_id: pid,
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
    };
    recentlyPlayed[pid] = [];
  }

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
      player_count: allPlayerIds.length,
      double_character_key: "character_TODO_SAME_CHAR_IN_SAME_TEAM",
      rp: 0,
      allowed_buckets: ["Any"],
      allowed_buckets_relaxed: ["Any"],
    },
    server_data: null,
    criteria_slug: "2v2-retail",
    cluster: req.body.data.MultiplayParams.MultiplayClusterSlug,
    players_connection_info: playersConnectionInfo,
    player_connections: playerConnections,
    players: playersObj,
    groups: [1],
    relationships: [],
    recently_played: recentlyPlayed,
    from_match: req.body.match,
    reuse_match: false,
    party_id: lobbyId || null,
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

  await queueMatch(aID, allPlayerIds, data.from_match, data.id, MATCH_TYPES.TWO_V_TWO);
}

export async function handle_cancel_matchmaking(req: Request<{ id: string }, {}, {}, {}>, res: Response) {
  const account = AuthUtils.DecodeClientToken(req);
  const aID = account.id;

  // Cancel matchmaking for ALL players in the lobby, not just the requester
  const lobbyId = await redisGetPlayerLobby(aID);
  const lobbyState = lobbyId ? await redisGetLobbyState(lobbyId) : null;
  const allPlayerIds = lobbyState ? lobbyState.playerIds : [aID];

  await cancelMatchmakingForAll(allPlayerIds, req.params.id);

  res.send({ body: {}, metadata: null, return_code: 0 });
}
