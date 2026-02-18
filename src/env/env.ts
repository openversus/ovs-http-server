import { configDotenv } from "dotenv";
import { cleanEnv, str, num, bool } from "envalid";

configDotenv({ path: ".env" });

const env = cleanEnv(process.env, {
  BANNED_NAMES_FILE: str({ default: "../data/banned_names.txt" }),
  CIDR_BANS_FILE: str({ default: "../data/cidr_bans.txt" }),
  DATA_ASSET_TOKEN: str(),
  EMULATE_P2: num({ default: 0 }),
  FORCE_CHANGE_NAMES_FILE: str({ default: "../data/force_change_names.txt" }),
  GAME_DOMAIN: str(),
  GAME_VERSION: str(),
  HTTP_PORT: num(),
  IP_BANS_FILE: str({ default: "../data/bans.txt" }),
  LOCAL_PUBLIC_IP: str(),
  MONGODB_URI: str(),
  REDIS: str(),
  REDIS_PORT: num(),
  REDIS_PW: str(),
  REDIS_USERNAME: str(),
  ROLLBACK_UDP_PORT_HIGH: num({ default: 57019 }),
  ROLLBACK_UDP_PORT_LOW: num({ default: 57000 }),
  USE_INTERNAL_ROLLBACK: num({ default: 0 }),
  USE_INTERNAL_ROLLBACK_CPP: num({ default: 0 }),
  UDP_PORT: num(),
  UDP_SERVER_IP: str(),
  UDP_SERVER_IP2: str(),
  VERBOSE_LOGGING: num({ default: 0 }),
  WB_DOMAIN: str(),
  WEBSOCKET_PORT: num(),
});

export default env;
