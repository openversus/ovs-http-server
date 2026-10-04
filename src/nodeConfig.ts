// P2P nodes' lockdown, the server's half (OVS.Rollback.Node: NodeLockdown). A node takes the settings every player in
// a match shares from GET /ovs_node_config, and its match configs from POST /ovs_register, only when the answer carries
// X-OVS-Signature: ECDSA P-256 over the exact body bytes, SHA-256, IEEE P1363 r||s, base64. The node is built with the
// public half of the key (the rollback repo's OVSRollbackNode/node-config-public-key.txt); the private half is
// P2P_NODE_SIGNING_KEY (PKCS#8 PEM text) or P2P_NODE_SIGNING_KEY_FILE (a path to it). Without a key /ovs_node_config
// answers 503 and /ovs_register answers unsigned: nodes then send every P2P match to the relay, which ignores the header.
//
// The update is P2P_NODE_CONFIG_FILE ({"version": N, "config": {sections, as the node's node.appsettings.json}}),
// relative to this file's directory unless absolute, read and signed at most once every five minutes.
import { createPrivateKey, sign, KeyObject } from "crypto";
import { readFileSync } from "fs";
import { isAbsolute, join } from "path";
import env from "./env/env";
import { logger } from "./config/logger";

export const SIGNATURE_HEADER = "X-OVS-Signature";
const CACHE_MS = 5 * 60 * 1000;
const logPrefix = "[nodeConfig]:";

let key: KeyObject | null | undefined;

/** The signing key, read once; null (logged once) when none is configured or it is not an ECDSA P-256 private key. */
function signingKey(): KeyObject | null {
  if (key !== undefined) return key;
  key = null;
  try {
    const pem = env.P2P_NODE_SIGNING_KEY || (env.P2P_NODE_SIGNING_KEY_FILE ? readFileSync(env.P2P_NODE_SIGNING_KEY_FILE, "utf-8") : "");
    if (!pem) {
      logger.warn(`${logPrefix} no P2P_NODE_SIGNING_KEY or P2P_NODE_SIGNING_KEY_FILE: nodes get no settings update and send every P2P match to the relay`);
      return key;
    }
    const candidate = createPrivateKey(pem);
    if (candidate.asymmetricKeyType !== "ec" || candidate.asymmetricKeyDetails?.namedCurve !== "prime256v1") {
      logger.error(`${logPrefix} the P2P node signing key is not an ECDSA P-256 key (${candidate.asymmetricKeyType} ${candidate.asymmetricKeyDetails?.namedCurve ?? ""}); signing nothing`);
      return key;
    }
    key = candidate;
  } catch (e) {
    logger.error(`${logPrefix} the P2P node signing key could not be read: ${(e as Error).message}; signing nothing`);
  }
  return key;
}

/** The X-OVS-Signature value for <body>, or null when there is no key. */
export function signBody(body: string): string | null {
  const k = signingKey();
  return k ? sign("sha256", Buffer.from(body, "utf-8"), { key: k, dsaEncoding: "ieee-p1363" }).toString("base64") : null;
}

export type SignedBody = { body: string; signature: string };

let cached: { at: number; answer: SignedBody | null } | null = null;

/** The signed update for GET /ovs_node_config, or null (logged when read) when there is none to give. */
export function nodeConfigAnswer(now: number = Date.now()): SignedBody | null {
  if (cached && now - cached.at < CACHE_MS) return cached.answer;
  cached = { at: now, answer: readNodeConfig() };
  return cached.answer;
}

function readNodeConfig(): SignedBody | null {
  const file = isAbsolute(env.P2P_NODE_CONFIG_FILE) ? env.P2P_NODE_CONFIG_FILE : join(__dirname, env.P2P_NODE_CONFIG_FILE);
  let parsed: any;
  try {
    parsed = JSON.parse(readFileSync(file, "utf-8"));
  } catch (e) {
    logger.error(`${logPrefix} the node settings update ${file} could not be read: ${(e as Error).message}`);
    return null;
  }
  if (!Number.isInteger(parsed?.version) || parsed?.config === null || typeof parsed?.config !== "object" || Array.isArray(parsed.config)) {
    logger.error(`${logPrefix} the node settings update ${file} must be {"version": <integer>, "config": {...}}`);
    return null;
  }
  // Signed as sent: the node checks the bytes it receives, so the body is made once and both go out together.
  const body = JSON.stringify({ version: parsed.version, config: parsed.config });
  const signature = signBody(body);
  if (!signature) return null;
  logger.info(`${logPrefix} node settings update version ${parsed.version} from ${file}, signed`);
  return { body, signature };
}
