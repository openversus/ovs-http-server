import { env } from "@mvsi/env";
import Elysia, { t } from "elysia";
import { randomUUID } from "node:crypto";
import { getCurrentCRC } from "../../data/config";
import { MAIN_APP, MVSI_HYDRA_WITH_JWT } from "../../middleware/middlewares";
import { HydraQueryPaginated } from "../../types";
import { getLobby, getLobbyIdFromCode, setPlayerConnectionInfo } from "../lobby/lobby.service";
import { submitArenaMatchStats } from "../lobby/arena.lobby.service";
import { getActiveMatch, notifyActiveMatchEnded } from "../matchmaking/matchmaking.service";
import { rematchDeclined } from "./matches.service";

const router = new Elysia().use(MVSI_HYDRA_WITH_JWT);

router.put(
  "/ssc/invoke/rematch_decline",
  async ({ claims, body }) => {
    await rematchDeclined(claims.id, body.ContainerMatchId);
    return { body: [], metadata: null, return_code: 0 };
  },
  {
    body: t.Object({
      ContainerMatchId: t.String(),
    }),
  },
);

router.put("/ssc/invoke/toast_player", async () => {
  // TODO : Implement
  return { body: [], metadata: null, return_code: 0 };
});

router.get(
  "/matches/all/:id",
  async () => {
    return {
      matches: [],
      total_matches: 0,
      current_page: 1,
      total_pages: 1,
    };
  },
  {
    query: HydraQueryPaginated,
    detail: {
      description: "Get all matches for account",
    },
  },
);

router.put(
  "/matches/:lobbyId",
  async ({ claims, params, body }) => {
    const lobby = await getLobby(params.lobbyId);
    if (!lobby) {
      return {};
    }

    // Store the player's region latencies on the lobby
    if (body.player_data) {
      const playerData = (
        body.player_data as Record<
          string,
          { game_server_region_data: { latency: number; region_id: string }[] }
        >
      )[claims.id];
      if (playerData?.game_server_region_data) {
        await setPlayerConnectionInfo(
          params.lobbyId,
          claims.id,
          playerData.game_server_region_data,
        );
      }
    }
    return {
      updated_at: new Date(),
      created_at: new Date(),
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
        Teams: lobby.Teams,
        LeaderID: lobby.LeaderID,
        LobbyType: 0,
        ReadyPlayers: lobby.ReadyPlayers,
        PlayerGameplayPreferences: lobby.PlayerGameplayPreferences,
        PlayerAutoPartyPreferences: lobby.PlayerAutoPartyPreferences,
        GameVersion: env.GAME_VERSION,
        HissCrc: getCurrentCRC(),
        Platforms: lobby.Platforms,
        AllMultiplayParams: {
          "1": {
            MultiplayClusterSlug: "ec2-us-east-1-dokken",
            MultiplayProfileId: "1252499",
            MultiplayRegionId: "",
          },
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
        LockedLoadouts: lobby.LockedLoadouts,
        ModeString: "TODO",
        IsLobbyJoinable: lobby.IsLobbyJoinable,
      },
      players: {
        all: [
          {
            account_id: claims.id,
            source: {},
            state: "join",
            data: {},
            identity: {
              username: claims.hydraUsername,
              avatar:
                "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png",
              default_username: true,
              personal_data: {},
              alternate: {
                wb_network: [
                  {
                    id: claims.id,
                    username: claims.username,
                    avatar: null,
                    email: null,
                  },
                ],
                steam: [
                  {
                    id: claims.steamId,
                    username: claims.username,
                    avatar:
                      "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg",
                    email: null,
                  },
                ],
              },
              usernames: [
                { auth: "hydra", username: claims.hydraUsername },
                { auth: "steam", username: claims.username },
                { auth: "wb_network", username: claims.username },
              ],
              platforms: ["steam"],
              current_platform: "steam",
              is_cross_platform: false,
            },
          },
        ],
        current: [claims.id],
        count: 1,
      },
      matchmaking: null,
      cluster: "ec2-us-east-1-dokken",
      last_warning_time: null,
      template: {
        type: "async",
        name: lobby.Template,
        slug: lobby.Template,
        min_players: 2,
        max_players: 2,
        game_server_integration_enabled: false,
        game_server_config: null,
        created_at: new Date(),
        updated_at: new Date(),
        data: {},
        id: "",
      },
      criteria: { slug: null },
      shortcode: null,
      id: params.lobbyId,
      access: "public",
    };
  },
  {
    body: t.Object(
      {
        player_data: t.Optional(
          t.Record(
            t.String(),
            t.Object({
              game_server_region_data: t.Array(
                t.Object({
                  latency: t.Number(),
                  region_id: t.String(),
                }),
              ),
            }),
          ),
        ),
      },
      { additionalProperties: true },
    ),
  },
);

router.get(
  "/matches/:lobbyId",
  async ({ params }) => {
    const lobbyId = await getLobbyIdFromCode(params.lobbyId);
    if (!lobbyId) {
      return {
        code: 404,
        msg: `No match was found with shortcode ${params.lobbyId}.`,
        hydra_error: 0,
        relying_party_error: 0,
        body: {},
      };
    }
    const lobby = await getLobby(lobbyId);
    if (!lobby) {
      return {
        code: 404,
        msg: `No match was found with shortcode ${params.lobbyId}.`,
        hydra_error: 0,
        relying_party_error: 0,
        body: {},
      };
    }
    return {
      updated_at: new Date(),
      created_at: new Date(),
      account_id: null,
      completion_time: null,
      name: randomUUID().slice(0, 30),
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
      server_data: lobby,
      players: {},
      matchmaking: null,
      cluster: "ec2-us-east-1-dokken",
      last_warning_time: null,
      template: {
        type: "async",
        name: lobby.Template,
        slug: lobby.Template,
        min_players: 1,
        max_players: 8,
        game_server_integration_enabled: false,
        game_server_config: null,
        created_at: new Date(),
        updated_at: new Date(),
        data: {},
        id: "",
      },
      criteria: { slug: null },
      shortcode: null,
      id: lobbyId,
      access: "public",
    };
  },
  {
    params: t.Object({
      lobbyId: t.String(),
    }),
  },
);

router.put(
  "/ssc/invoke/submit_end_of_match_stats",
  async ({ body }) => {
    const match = await getActiveMatch(body.ContainerMatchId);
    if (match?.GameplayConfig.ArenaId) {
      await submitArenaMatchStats(body.ContainerMatchId, body.EndOfMatchStats, body.MatchLength);
    }
    return { body: {}, metadata: null, return_code: 0 };
  },
  {
    body: t.Object({
      ContainerMatchId: t.String(),
      EndOfMatchStats: t.Object({
        PlayerMissionUpdates: t.Record(t.String(), t.Record(t.String(), t.Number())),
        Score: t.Array(t.Number()),
        WinningTeamIndex: t.Number(),
      }),
      MatchLength: t.Number(),
    }),
  },
);

const mvsi_match_routes = new Elysia();

mvsi_match_routes.post(
  "/mvsi_register",
  async ({ body }) => {
    const match = await getActiveMatch(body.matchId);
    if (match?.matchKey !== body.key) {
      return "";
    }
    const playerKeys = Object.keys(match.GameplayConfig.GameplayConfig.Players);
    const players = playerKeys.map((playerKey) => {
      const player = match.GameplayConfig.GameplayConfig.Players[playerKey];
      return {
        player_index: player.PlayerIndex,
        ip: player.Ip,
        is_host: player.IsHost,
      };
    });
    const humanCount = playerKeys.filter(
      (k) => !match.GameplayConfig.GameplayConfig.Players[k].bIsBot,
    ).length;
    const result = {
      max_players: humanCount,
      match_duration: 36000,
      players,
    };
    return result;
  },
  {
    body: t.Object({
      matchId: t.String(),
      key: t.String(),
    }),
  },
);

mvsi_match_routes.post(
  "/mvsi_end_match",
  async ({ body }) => {
    const match = await getActiveMatch(body.matchId);
    if (match?.matchKey !== body.key) {
      return "";
    }
    if (match) {
      await notifyActiveMatchEnded(
        Object.keys(match.GameplayConfig.GameplayConfig.Players).map(
          (p) => match.GameplayConfig.GameplayConfig.Players[p].AccountId,
        ),
        match.GameplayConfig.GameplayConfig.MatchId,
      );
      return "";
    }
  },
  {
    body: t.Object({
      matchId: t.String(),
      key: t.String(),
    }),
  },
);

MAIN_APP.use(mvsi_match_routes);
MAIN_APP.use(router);
