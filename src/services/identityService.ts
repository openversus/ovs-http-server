import { Request } from "express";
import { redisClient, RedisPlayerConnection as FullRedisPlayerConnection } from "../config/redis";
import { logger } from "../config/logger";
import * as AuthUtils from "../utils/auth";
import { IAccountToken } from "../types/AccountToken";

/**
 * redisClient.hGetAll() returns `{}` when the key doesn't exist and partial data
 * when some hash fields are missing. The canonical `RedisPlayerConnection` in
 * config/redis.ts extends AccountToken with all fields declared non-nullable,
 * which lies to TypeScript about runtime reality. Wrap in Partial<> so every
 * field is correctly treated as possibly-undefined. Also exported for downstream
 * consumers that were importing our previous local redefinition.
 */
export type RedisPlayerConnection = Partial<FullRedisPlayerConnection>;

const serviceName = "Services.Identity";
const logPrefix = `[${serviceName}]:`;

/**
 * Routes whose identity resolution we don't want logged — chatty polling endpoints
 * where the DLL doesn't attach headers and always lands on IP fallback.
 * Adding entries here silences ALL resolver logs for matching routes.
 */
const SILENT_ROUTES = [
  "/ovs/notifications",
  "/ovs/my-friends",
];

function isSilentRoute(routeTag: string | null | undefined): boolean {
  if (!routeTag || typeof routeTag !== "string") return false;
  return SILENT_ROUTES.some(r => routeTag === r || routeTag.startsWith(r));
}

/** Safely attempt to decode the JWT on a request. Returns null if missing/invalid. */
function safeDecodeToken(req: Request): IAccountToken | null {
  // Middleware sets req.token when valid; prefer that.
  // Explicit null/undefined/empty-string guard on id — optional chaining handles
  // the nested nullability but being explicit makes the intent obvious.
  const middlewareToken = (req as any).token as IAccountToken | undefined | null;
  if (middlewareToken && middlewareToken.id) return middlewareToken;

  // Fallback: try to decode raw header (for edge cases)
  try {
    const decoded = AuthUtils.DecodeClientToken(req as any);
    if (decoded && decoded.id) return decoded;
    return null;
  } catch {
    return null;
  }
}

/** Safely read IP from the request without throwing. */
function safeGetIP(req: Request): string | null {
  try {
    return AuthUtils.GetReqIP(req as any) || null;
  } catch {
    return null;
  }
}

/**
 * Resolve the account connection for a request, trying identifiers in order
 * of uniqueness/reliability. Falls back through:
 *   1. JWT account ID (most handler calls)
 *   2. SteamID  (unique per Steam user)
 *   3. EpicID   (unique per Epic user)
 *   4. HardwareID (unique per device, survives account changes)
 *   5. IP       (ambiguous for same-household — last resort)
 *
 * Returns null if no identifier resolves to a known connection.
 */
export async function resolveAccountFromRequest(req: Request): Promise<RedisPlayerConnection | null> {
  const token = safeDecodeToken(req);
  const routeTag = (req as any).route?.path || req.path || "?";
  const silent = isSilentRoute(routeTag);

  // 1. Direct account ID from JWT (happy path for authenticated handler calls)
  if (token?.id) {
    const conn = (await redisClient.hGetAll(`connections:${token.id}`)) as RedisPlayerConnection;
    if (conn?.id) {
      if (!silent) logger.info(`${logPrefix} [${routeTag}] resolved via JWT id=${conn.id}`);
      return conn;
    }
    if (!silent) logger.debug(`${logPrefix} [${routeTag}] JWT id=${token.id} had no connections:* record, falling through`);
  }

  // Helper: look up accountId from an index key, then fetch connection
  const resolveByIndex = async (indexKey: string): Promise<RedisPlayerConnection | null> => {
    const accountId = await redisClient.get(indexKey);
    if (!accountId) return null;
    const conn = (await redisClient.hGetAll(`connections:${accountId}`)) as RedisPlayerConnection;
    return conn?.id ? conn : null;
  };

  // 2. SteamID (from header or token)
  const steamId = (req.header("x-steam-id") as string) || token?.steamId;
  if (steamId) {
    const hit = await resolveByIndex(`identity:steam:${steamId}`);
    if (hit) {
      if (!silent) logger.info(`${logPrefix} [${routeTag}] resolved via SteamID=${steamId} → id=${hit.id}`);
      return hit;
    }
  }

  // 3. EpicID
  const epicId = (req.header("x-epic-id") as string) || token?.epicId;
  if (epicId) {
    const hit = await resolveByIndex(`identity:epic:${epicId}`);
    if (hit) {
      if (!silent) logger.info(`${logPrefix} [${routeTag}] resolved via EpicID=${epicId} → id=${hit.id}`);
      return hit;
    }
  }

  // 4. HardwareID (survives account resets, still unique per device)
  const hardwareId = (req.header("x-hw-id") as string) || token?.hardwareId;
  if (hardwareId) {
    const hit = await resolveByIndex(`identity:hw:${hardwareId}`);
    if (hit) {
      if (!silent) logger.info(`${logPrefix} [${routeTag}] resolved via HardwareID=${hardwareId} → id=${hit.id}`);
      return hit;
    }
  }

  // 5. IP — last resort. Try the token's current_ip first (set at /access time,
  //    which is the IP the connections:${ip} record was written under), then req.ip.
  //    Same-household collision bucket: whoever hit /access most recently wins.
  //    Kept for legacy clients that don't send platform IDs.
  const tokenIp = token?.current_ip ? token.current_ip.replace(/^::ffff:/, "") : null;
  const reqIp = safeGetIP(req);
  const ipsToTry = [tokenIp, reqIp].filter((v, i, a) => v && a.indexOf(v) === i) as string[];
  for (const ip of ipsToTry) {
    const conn = (await redisClient.hGetAll(`connections:${ip}`)) as RedisPlayerConnection;
    if (conn?.id) {
      if (!silent) logger.warn(`${logPrefix} [${routeTag}] resolved via IP fallback ip=${ip} → id=${conn.id}`);
      return conn;
    }
  }
  const ip = ipsToTry[0] || null;

  if (!silent) logger.warn(`${logPrefix} [${routeTag}] UNRESOLVED: jwt=${token?.id ?? "-"} steam=${steamId ?? "-"} epic=${epicId ?? "-"} hw=${hardwareId ?? "-"} ip=${ip ?? "-"}`);
  return null;
}

/**
 * Convenience: resolve just the account ID string (or null if not found).
 */
export async function resolveAccountId(req: Request): Promise<string | null> {
  const conn = await resolveAccountFromRequest(req);
  return conn?.id || null;
}

/**
 * Header-free variant for non-Express contexts (WebSockets, raw IncomingMessage).
 * Tries the identifiers in the same order: accountId → steamId → epicId → hardwareId → ip.
 */
export async function resolveAccountByIdentifiers(opts: {
  accountId?: string | null;
  steamId?: string | null;
  epicId?: string | null;
  hardwareId?: string | null;
  ip?: string | null;
}): Promise<RedisPlayerConnection | null> {
  if (opts.accountId) {
    const conn = (await redisClient.hGetAll(`connections:${opts.accountId}`)) as RedisPlayerConnection;
    if (conn?.id) {
      logger.debug(`${logPrefix} [byIdentifiers] resolved via accountId=${opts.accountId}`);
      return conn;
    }
  }

  const byIndex = async (indexKey: string): Promise<RedisPlayerConnection | null> => {
    const accountId = await redisClient.get(indexKey);
    if (!accountId) return null;
    const conn = (await redisClient.hGetAll(`connections:${accountId}`)) as RedisPlayerConnection;
    return conn?.id ? conn : null;
  };

  if (opts.steamId) {
    const hit = await byIndex(`identity:steam:${opts.steamId}`);
    if (hit) {
      logger.debug(`${logPrefix} [byIdentifiers] resolved via SteamID=${opts.steamId} → id=${hit.id}`);
      return hit;
    }
  }
  if (opts.epicId) {
    const hit = await byIndex(`identity:epic:${opts.epicId}`);
    if (hit) {
      logger.debug(`${logPrefix} [byIdentifiers] resolved via EpicID=${opts.epicId} → id=${hit.id}`);
      return hit;
    }
  }
  if (opts.hardwareId) {
    const hit = await byIndex(`identity:hw:${opts.hardwareId}`);
    if (hit) {
      logger.debug(`${logPrefix} [byIdentifiers] resolved via HardwareID=${opts.hardwareId} → id=${hit.id}`);
      return hit;
    }
  }
  if (opts.ip) {
    const conn = (await redisClient.hGetAll(`connections:${opts.ip}`)) as RedisPlayerConnection;
    if (conn?.id) {
      logger.debug(`${logPrefix} [byIdentifiers] resolved via IP fallback ip=${opts.ip} → id=${conn.id}`);
      return conn;
    }
  }
  logger.warn(`${logPrefix} [byIdentifiers] UNRESOLVED: acct=${opts.accountId ?? "-"} steam=${opts.steamId ?? "-"} epic=${opts.epicId ?? "-"} hw=${opts.hardwareId ?? "-"} ip=${opts.ip ?? "-"}`);
  return null;
}

/**
 * Write the identity index keys that feed the fallback lookups above.
 * Called from /access after a player logs in and their PlayerTester is saved.
 * Indexes TTL'd at 30 days — refreshed on every login.
 */
/**
 * Event-based admin cookie invalidation:
 * Bump the per-IP "accounts changed" timestamp. Called from /access when a NEW
 * account appears at an IP, or an existing account's IP changes. Admin web cookies
 * (ovs_web_account) issued before this timestamp are forced to re-verify, so a
 * stale cookie never lingers after the IP's account lineup shifts.
 *
 * The admin routes in server.ts read `admin:ip_changed_at:${ip}` and compare to the
 * cookie's `iat`. See isAdminCookieStale() in server.ts.
 */
export async function bumpIpAccountsChangedAt(ip: string): Promise<void> {
  if (!ip) return;
  try {
    await redisClient.set(`admin:ip_changed_at:${ip}`, String(Date.now()));
    logger.debug(`${logPrefix} bumped admin:ip_changed_at:${ip}`);
  } catch (e) {
    logger.error(`${logPrefix} Error bumping admin:ip_changed_at:${ip}: ${e}`);
  }
}

export async function writeIdentityIndexes(
  accountId: string,
  steamId?: string | null,
  epicId?: string | null,
  hardwareId?: string | null,
): Promise<void> {
  const TTL = 60 * 60 * 24 * 30; // 30 days
  const written: string[] = [];
  try {
    if (steamId) {
      await redisClient.set(`identity:steam:${steamId}`, accountId, { EX: TTL });
      written.push(`steam=${steamId}`);
    }
    if (epicId) {
      await redisClient.set(`identity:epic:${epicId}`, accountId, { EX: TTL });
      written.push(`epic=${epicId}`);
    }
    if (hardwareId) {
      await redisClient.set(`identity:hw:${hardwareId}`, accountId, { EX: TTL });
      written.push(`hw=${hardwareId}`);
    }
    logger.info(`${logPrefix} wrote identity indexes for ${accountId}: [${written.join(", ") || "none"}]`);
  } catch (e) {
    logger.error(`${logPrefix} Error writing identity indexes for ${accountId}: ${e}`);
  }
}
