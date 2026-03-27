import { env } from "@mvsi/env";
import { logger } from "@mvsi/logger";
import Elysia, { t } from "elysia";
import { getCurrentCRC, MATCHMAKING_CRC } from "../../data/config";
import { MAIN_APP, MVSI_HYDRA_WITH_JWT } from "../../middleware/middlewares";
import { MVSTime } from "../../utils/date";
import { GameConfigService } from "./gameConfig.service";

const router = new Elysia().use(MVSI_HYDRA_WITH_JWT);
const noAuthRouter = new Elysia();

router.put(
  "/ssc/invoke/hiss_amalgamation",
  async ({ body }) => {
    if (body.Crc !== getCurrentCRC()) {
      const gameConfig = await GameConfigService.generateGameConfig_hiss();
      return {
        body: gameConfig,
        metadata: null,
        return_code: 200,
      };
    }
    return {
      body: { Crc: getCurrentCRC(), MatchmakingCrc: MATCHMAKING_CRC },
      metadata: null,
      return_code: 304,
    };
  },
  {
    body: t.Object({
      Crc: t.Number(),
    }),
    detail: {
      summary:
        "Get current game config. Returns HTTP 304, if client CRC matches server CRC. Game configs include which assets and features are enabled/disabled",
    },
  },
);

router.get(
  "/ssc/invoke/hiss_amalgamation",
  async () => {
    const gameConfig = await GameConfigService.generateGameConfig_hiss();
    return {
      body: gameConfig,
      metadata: null,
      return_code: 200,
    };
  },
  {
    detail: {
      summary: "Get current game config. Does not check against server CRC",
    },
  },
);

router.get("/ssc/invoke/get_country_code", () => {
  return { body: { region: "US" }, metadata: null, return_code: 0 };
});

router.put("/ssc/invoke/game_launch_event", () => {
  return "";
});

router.post("/ssc/invoke/attempt_daily_refresh", () => {
  return {
    body: {
      ServerTimeUtc: { _hydra_unix_date: MVSTime(new Date()) },
      CurrentSeason: "Season:SeasonFive",
      NextDailyRefreshTime: { _hydra_unix_date: MVSTime(new Date()) + 86400 },
      NextWeeklyRefreshTime: { _hydra_unix_date: MVSTime(new Date()) + 604800 },
      FreeCharacterRotation: [],
      ReturnData: {},
      PlayerMissionObject: {
        MissionControllerContainers: {},
        ClaimLocks: {},
      },
    },
    metadata: null,
    return_code: 0,
  };
});

noAuthRouter.post("/syncAsset", async ({ body, headers, status }) => {
  logger.info("Trying syncAsset");
  try {
    if (headers.authorization !== `Bearer ${env.DATA_ASSET_TOKEN}`) {
      status(403);
      return;
    }
    await GameConfigService.syncAsset(body as any);
    status(200);
  } catch (e) {
    status(404);
    //@ts-expect-error
    return e.codeName;
  }
  return;
});

noAuthRouter.get("/v1/fleets*", async () => {
  return {
    servers: [
      // WEST US
      {
        locationid: 0,
        regionid: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
        ipv4: "108.61.219.200",
        ipv6: "",
        port: 9000,
      },
      // CENTRAL US
      {
        locationid: 1,
        regionid: "19bf18ce-f21f-11ea-b94f-f946c68d5a4f",
        ipv4: "107.191.51.12",
        ipv6: "",
        port: 9000,
      },
      // EAST US
      {
        locationid: 2,
        regionid: "19c714ff-f21f-11ea-b144-4d87911ee195",
        ipv4: "108.61.149.182",
        ipv6: "",
        port: 9000,
      },
      // MEXICO
      {
        locationid: 3,
        regionid: "657d35f8-ca5e-11ec-85a7-b6a275757dc0",
        ipv4: "216.238.66.16",
        ipv6: "",
        port: 9000,
      },
      // SA - SAO PAULO
      {
        locationid: 5,
        regionid: "0d77609a-c3c5-11eb-8890-0242ac110002",
        ipv4: "216.238.98.118",
        ipv6: "",
        port: 9000,
      },
      // EU - MANCHESTER
      {
        locationid: 6,
        regionid: "19c32880-f21f-11ea-a907-512b3194e649",
        ipv4: "64.176.178.136",
        ipv6: "",
        port: 9000,
      },
      // Oceanic - SYDNEY
      {
        locationid: 7,
        regionid: "19cf42e3-f21f-11ea-a4fe-05a850423fbf",
        ipv4: "108.61.212.117",
        ipv6: "",
        port: 9000,
      },
      // SEA - TOKYO
      {
        locationid: 8,
        regionid: "19c9c88a-f21f-11ea-bbf4-cb9c4fdeb10a",
        ipv4: "108.61.201.151",
        ipv6: "",
        port: 9000,
      },
    ],
  };
});

MAIN_APP.use(router);
MAIN_APP.use(noAuthRouter);
