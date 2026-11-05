// Helpers for the PlayerCounters sidecar collection.
//
// All read-side paths go through `getCounters()` which lazily creates a doc
// with defaults (100 toasts, never-claimed bonus) on first access. This means
// every existing account "appears" to start at 100 the first time they touch
// the new system, without a migration backfill.

import { PlayerCounters, PlayerCountersModel } from "../database/PlayerCounters";
import { logger } from "../config/logger";

const serviceName = "Data.PlayerCounters";
const logPrefix = `[${serviceName}]:`;

// Daily login grant amount — kept here as a constant for now; promote to env
// or admin-config when we add a knob.
export const DAILY_TOAST_BONUS = 10;
export const STARTING_TOAST_COUNT = 100;

// ---------------------------------------------------------------------------
// 11am America/Chicago boundary math
// ---------------------------------------------------------------------------
// The grant cadence is "once per day after 11am Central, only on login."
// We model this as: each calendar day in Chicago has one boundary at 11:00
// local time. If the player's lastToastBonusUnix < the most-recent boundary,
// they're eligible. DST is handled by re-probing the offset each call.

function chicago11amUtcMillis(chicagoDate: string /* "YYYY-MM-DD" */): number {
  // Probe noon UTC on this date and ask Chicago what hour it sees.
  // Chicago's hour at noon UTC = 12 + offset. Solve for the UTC hour
  // corresponding to 11am Chicago.
  const probe = new Date(`${chicagoDate}T12:00:00Z`);
  const probeChicagoHour = parseInt(
    new Intl.DateTimeFormat("en-US", {
      timeZone: "America/Chicago",
      hour: "2-digit",
      hour12: false,
    }).format(probe),
    10,
  );
  // probeChicagoHour ∈ {6, 7} (CST=-6 → 6, CDT=-5 → 7).
  // UTC hour for 11am Chicago = 11 + (12 - probeChicagoHour) = 23 - probeChicagoHour.
  const utcHour = 23 - probeChicagoHour;
  const [y, m, d] = chicagoDate.split("-").map(Number);
  return Date.UTC(y, m - 1, d, utcHour, 0, 0);
}

/**
 * Returns unix seconds for the most-recent 11:00 America/Chicago tick.
 * If "right now" is past today's 11am-CT, returns today's; otherwise yesterday's.
 */
export function getMostRecent11amCentralUnix(): number {
  const now = new Date();
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/Chicago",
    year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", hour12: false,
  }).formatToParts(now);
  const get = (t: string) => parts.find((p) => p.type === t)!.value;
  const chicagoDate = `${get("year")}-${get("month")}-${get("day")}`;
  const chicagoHour = parseInt(get("hour"), 10);

  let targetDate = chicagoDate;
  if (chicagoHour < 11) {
    // Pre-11am Chicago → most-recent boundary was *yesterday's* 11am Chicago.
    const [y, m, d] = chicagoDate.split("-").map(Number);
    const yest = new Date(Date.UTC(y, m - 1, d) - 86400_000);
    const yy = yest.getUTCFullYear();
    const mm = String(yest.getUTCMonth() + 1).padStart(2, "0");
    const dd = String(yest.getUTCDate()).padStart(2, "0");
    targetDate = `${yy}-${mm}-${dd}`;
  }
  return Math.floor(chicago11amUtcMillis(targetDate) / 1000);
}

// ---------------------------------------------------------------------------
// Read / mutate helpers
// ---------------------------------------------------------------------------

/**
 * Returns the player's counter doc, creating with defaults on first access.
 * Uses findOneAndUpdate with upsert + setOnInsert so concurrent first-access
 * calls don't race-create duplicate docs.
 */
export async function getCounters(accountId: string): Promise<PlayerCounters> {
  const doc = await PlayerCountersModel.findOneAndUpdate(
    { accountId },
    {
      $setOnInsert: {
        accountId,
        match_toasts: STARTING_TOAST_COUNT,
        lastToastBonusUnix: 0,
      },
    },
    { upsert: true, new: true },
  ).lean();
  return doc as unknown as PlayerCounters;
}

/**
 * Atomic increment (or decrement, with negative delta) of match_toasts.
 * Returns the post-update count. Never drops below 0 — a decrement that
 * would underflow is no-op'd and we return the current count instead.
 */
export async function adjustMatchToasts(accountId: string, delta: number): Promise<number> {
  if (delta === 0) {
    const cur = await getCounters(accountId);
    return cur.match_toasts;
  }
  if (delta < 0) {
    // Ensure the doc exists with defaults BEFORE the conditional decrement.
    // Without this, a brand-new account's first negative adjust silently
    // no-ops: findOneAndUpdate doesn't match (no doc), the fallback
    // getCounters then creates the doc at 100, and the spend is lost.
    // Doing getCounters first means the conditional $inc has a real doc
    // to match against.
    await getCounters(accountId);
    const updated = await PlayerCountersModel.findOneAndUpdate(
      { accountId, match_toasts: { $gte: -delta } },
      { $inc: { match_toasts: delta } },
      { new: true },
    ).lean();
    if (updated) return (updated as any).match_toasts as number;
    // Doc exists but balance is insufficient — return current count unchanged.
    const cur = await getCounters(accountId);
    logger.warn(`${logPrefix} match_toasts decrement (${delta}) skipped for ${accountId}; balance ${cur.match_toasts} insufficient`);
    return cur.match_toasts;
  }
  // delta > 0: ensure doc exists with defaults, then increment.
  await getCounters(accountId);
  const updated = await PlayerCountersModel.findOneAndUpdate(
    { accountId },
    { $inc: { match_toasts: delta } },
    { new: true },
  ).lean();
  return (updated as any).match_toasts as number;
}

/**
 * Attempts to grant the daily toast bonus.
 *
 * Returns `{ granted: number, newCount: number, boundaryUnix: number }`.
 *   - granted = 0 if the player has already claimed for this boundary
 *   - granted = DAILY_TOAST_BONUS if eligible
 *
 * Uses a single atomic findOneAndUpdate keyed on `lastToastBonusUnix < boundary`
 * so concurrent calls (multi-instance, rapid /access retries) can't double-grant.
 */
export async function tryGrantDailyToastBonus(accountId: string): Promise<{
  granted: number;
  newCount: number;
  boundaryUnix: number;
}> {
  const boundaryUnix = getMostRecent11amCentralUnix();
  // Ensure doc exists with defaults first.
  await getCounters(accountId);

  const updated = await PlayerCountersModel.findOneAndUpdate(
    { accountId, lastToastBonusUnix: { $lt: boundaryUnix } },
    {
      $inc: { match_toasts: DAILY_TOAST_BONUS },
      $set: { lastToastBonusUnix: boundaryUnix },
    },
    { new: true },
  ).lean();

  if (updated) {
    logger.info(`${logPrefix} Granted daily +${DAILY_TOAST_BONUS} toasts to ${accountId} (new count: ${(updated as any).match_toasts}, boundary: ${boundaryUnix})`);
    return {
      granted: DAILY_TOAST_BONUS,
      newCount: (updated as any).match_toasts as number,
      boundaryUnix,
    };
  }

  // Already claimed for this boundary — fetch current count for the caller.
  const cur = await getCounters(accountId);
  return { granted: 0, newCount: cur.match_toasts, boundaryUnix };
}
