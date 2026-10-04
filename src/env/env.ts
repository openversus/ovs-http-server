import { configDotenv } from "dotenv";
import { cleanEnv, str, num, bool, makeValidator, EnvError } from "envalid";

configDotenv({ path: ".env" });

/** Why a JWT_SECRET value must not be used, or null: at least 32 characters, and never the value once hardcoded in the public repo. */
export function jwtSecretProblem(value: string): string | null {
  if (value === "SHHHH!!") return "JWT_SECRET is the old public value; set a new random one";
  if (value.length < 32) return "JWT_SECRET must be at least 32 characters (use a long random value)";
  return null;
}

/**
 * Why an ACCESS_TOKEN_TTL value must not be used, or null. A whole number of seconds, or one followed
 * by s, m, h or d (2m, 24h, 7d); empty means game session tokens never expire (the default is 24h). Checked at startup because jwt.sign throws on a value it cannot read,
 * and that would fail every login.
 */
export function accessTokenTtlProblem(value: string): string | null {
  if (value === "" || /^[1-9][0-9]*[smhd]?$/.test(value)) return null;
  return `ACCESS_TOKEN_TTL "${value}" is not a lifetime: use seconds (86400) or a number with s, m, h or d (24h, 7d), or leave it empty`;
}

/**
 * The jwt.sign expiresIn for ACCESS_TOKEN_TTL: undefined when empty (no expiry), seconds as a number
 * for a bare number (jsonwebtoken would read the string "120" as 120 milliseconds), else the string.
 */
export function accessTokenExpiresIn(value: string): number | string | undefined {
  if (value === "") return undefined;
  return /^[0-9]+$/.test(value) ? Number(value) : value;
}

const accessTokenTtl = makeValidator<string>((value) => {
  const problem = accessTokenTtlProblem(value);
  if (problem) throw new EnvError(problem);
  return value;
});

const jwtSecret = makeValidator<string>((value) => {
  const problem = jwtSecretProblem(value);
  if (problem) throw new EnvError(problem);
  return value;
});

const env = cleanEnv(process.env, {
  ACCESS_TOKEN_TTL: accessTokenTtl({ default: "24h" }),
  BANNED_NAMES_FILE: str({ default: "../data/banned_names.txt" }),
  CIDR_BANS_FILE: str({ default: "../data/cidr_bans.txt" }),
  DATA_ASSET_TOKEN: str(),
  DEFAULT_ELO: num({ default: 0 }),
  ELO_DIVISOR: num({ default: 800 }),
  EMULATE_P2: num({ default: 0 }),
  EPICID_BANS_FILE: str({ default: "../data/epicid_bans.txt" }),
  FORCE_CHANGE_NAMES_FILE: str({ default: "../data/force_change_names.txt" }),
  GAME_DOMAIN: str(),
  GAME_VERSION: str(),
  HASHBANS_FILE: str({ default: "../data/hashbans.txt" }),
  HTTP_PORT: num(),
  IP_BANS_FILE: str({ default: "../data/bans.txt" }),
  K_PROVISIONAL: num({ default: 64 }),
  K_1V1: num({ default: 32 }),
  K_2V2: num({ default: 24 }),
  LOCAL_PUBLIC_IP: str(),
  // Oldest client version allowed into matches and lobbies (a release version such as
  // 2026.09.27.1). Empty: no minimum, but while CLIENT_VERSION_CHECK is on a client must
  // still have registered through /api/identify, which old C++ clients never do.
  MIN_CLIENT_VERSION: str({ default: "" }),
  // The Steam app a login's Steam ticket must be for (MultiVersus = 1818750). 0 accepts
  // a signed ticket for any app.
  STEAM_APP_ID: num({ default: 1818750 }),
  // Master switch for the client gate (version minimum + /api/identify check).
  // false lets any client version play; set true (or remove) to enforce again.
  CLIENT_VERSION_CHECK: bool({ default: true }),
  // The GitHub "owner/repo" whose latest release /ovs/client-version offers. Local testing
  // only: point it at a fork's release to test the client's .asi and pak updates.
  CLIENT_RELEASE_REPO: str({ default: "openversus/ovs-client" }),
  // Signs every token (game sessions, websocket handshake, /api/identify, account-picker
  // cookie). Required, no default: a long random value, the same on index, mm and ws, never
  // committed. Changing it logs everyone out once.
  JWT_SECRET: jwtSecret(),
  MATCHUPDATEKEY: str({ default: "MisconfiguredMatchUpdateKey" }),
  // true: hand players their daily/weekly/FTUE missions. Off = no missions at all.
  MISSIONS_ENABLED: bool({ default: false }),
  // true: serve the retail client's test/unfinished characters (see data/testCharacters.ts).
  ENABLE_TEST_CHARACTERS: bool({ default: false }),
  MONGODB_URI: str(),
  ON_DEMAND_ROLLBACK: num({ default: 0 }),
  ON_DEMAND_ROLLBACK_PORT_LOW: num({ default: 60000 }),
  ON_DEMAND_ROLLBACK_PORT_HIGH: num({ default: 64000 }),
  OVS_SERVER: str({ default: "http://localhost:8000" }),
  PROVISIONAL_GAME_THRESHOLD: num({ default: 20 }),
  REDIS: str(),
  REDIS_PORT: num(),
  REDIS_PW: str(),
  REDIS_USERNAME: str(),
  ROLLBACK_UDP_PORT_HIGH: num({ default: 57019 }),
  ROLLBACK_UDP_PORT_LOW: num({ default: 57000 }),
  SECURE_WEBSOCKET_PORT: num({ default: 5000 }),
  STEAMID_BANS_FILE: str({ default: "../data/steamid_bans.txt" }),
  USE_INTERNAL_ROLLBACK: num({ default: 0 }),
  // P2P rollback: every match with a human player is told to connect to the node on their own
  // machine (127.0.0.1:P2P_NODE_PORT); the host's node runs the engine, the others forward to it. See SERVER_FLAGS.md.
  P2P_ROLLBACK: num({ default: 0 }),
  P2P_NODE_PORT: num({ default: 41234 }),
  // The nodes' lockdown (src/nodeConfig.ts): the private key /ovs_node_config and /ovs_register are signed with
  // (PKCS#8 PEM text, or a path to it), and the settings update nodes fetch at startup. No key: nodes fall back to
  // the relay for every P2P match.
  P2P_NODE_SIGNING_KEY: str({ default: "" }),
  P2P_NODE_SIGNING_KEY_FILE: str({ default: "" }),
  P2P_NODE_CONFIG_FILE: str({ default: "data/node-config.json" }),
  USE_INTERNAL_ROLLBACK_CPP: num({ default: 0 }),
  USE_SECURE_WEBSOCKET: num({ default: 0 }),
  UDP_PORT: num(),
  UDP_SERVER_IP: str(),
  UDP_SERVER_IP2: str(),
  VERBOSE_LOGGING: num({ default: 0 }),
  ADMIN_PASSWORD: str({ default: "changeme" }),
  WEBHOOK_HMAC_SECRET: str({ default: "CHANGEME" }),
  WEBHOOK_HOST: str({ default: "localhost" }),
  WEBHOOK_PORT: num({ default: 9001 }),
  WEBHOOK_DEPLOY_PATH: str({ default: "/hooks/deploy-rollback-server"}),
  WEBHOOK_DESTROY_PATH: str({ default: "/hooks/destroy-rollback-server" }),
  DEPLOY_KEY: str({ default: "CHANGEME" }),
  DEPLOY_ROLLBACK_DEFAULTS_FILE: str({ default: "../data/deploy-rollback-defaults.json" }),
  WB_DOMAIN: str(),
  WEBSOCKET_PORT: num(),
});

export default env;
