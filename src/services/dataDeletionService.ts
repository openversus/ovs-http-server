// Data deletion / anonymization service.
//
// Called when an admin processes a DataDeletionRequest. Deletes everything
// keyed by the player's accountId across MongoDB + Redis, then anonymizes
// the player's references in MatchArchive entries (kept for game-integrity
// + statistical-purposes carve-outs in GDPR Art 17(3)).
//
// Conventions:
//   - DELETE: collections/keys whose document IS the player's data
//   - ANONYMIZE: collections that record interactions OTHER players also have
//     a stake in (matches, sets, leaderboards). The deleted user becomes
//     `deleted_user_<8hex>` so historical records stay coherent for the
//     other participants but are no longer identifiable.

import { Types } from "mongoose";
import { randomBytes } from "crypto";
import { Worker } from "node:worker_threads";
import * as path from "node:path";
import { logger } from "../config/logger";
import { redisClient } from "../config/redis";
import { PlayerTesterModel } from "../database/PlayerTester";
import { EloRatingModel } from "../database/EloRating";
import { PlayerStatsModel } from "../database/PlayerStats";
import { CosmeticsModel } from "../database/Cosmetics";
import { FriendListModel } from "../database/FriendList";
import { MatchArchiveModel } from "../database/MatchArchive";

const serviceName = "Services.DataDeletion";
const logPrefix = `[${serviceName}]:`;

export interface DeletionReport {
  accountId: string;
  anonymizedDisplayName: string;
  /** Number of records affected per Mongo collection. */
  mongo: Record<string, number>;
  /** Number of Redis keys removed (best-effort, pattern-matched). */
  redisKeysDeleted: number;
  /** MatchArchive entries that had this player's references anonymized. */
  archivesAnonymized: number;
  /** MatchArchive entries that failed to decompress/recompress (left untouched). */
  archiveFailures: number;
  errors: string[];
}

function makeAnonymizedName(): string {
  return `deleted_user_${randomBytes(4).toString("hex")}`;
}

async function deleteRedisKeysByPattern(pattern: string): Promise<number> {
  let cursor = 0;
  let total = 0;
  do {
    const result = await redisClient.scan(cursor, { MATCH: pattern, COUNT: 200 });
    cursor = Number(result.cursor);
    if (result.keys.length > 0) {
      await redisClient.del(result.keys);
      total += result.keys.length;
    }
  } while (cursor !== 0);
  return total;
}

// ── Worker pool helper ──
//
// Spawns a single worker_threads worker that runs the CPU-bound zstd
// decompress/walk/recompress for one archive at a time. Main thread does
// Mongo I/O + queueing; worker does CPU work. Net effect: total wallclock
// for a deletion is similar to before, but the main event loop stays free
// the entire time, so live matches and incoming HTTP requests are not
// blocked while a deletion runs.
//
// Resolves the worker path with a `.ts` fallback for dev (swc-node/register
// transpiles at runtime via execArgv) and `.js` for prod builds emitted to
// build/. Throws if neither exists so the failure is loud at startup of
// the deletion rather than mid-loop.
class ArchiveAnonymizerWorker {
  private worker: Worker;
  private nextId = 1;
  private pending = new Map<number, { resolve: (m: any) => void; reject: (e: any) => void }>();
  private terminated = false;

  constructor() {
    const tsPath = path.resolve(__dirname, "../workers/anonymizeArchive.worker.ts");
    const jsPath = path.resolve(__dirname, "../workers/anonymizeArchive.worker.js");
    const fs = require("fs") as typeof import("fs");
    const useTs = fs.existsSync(tsPath);
    const workerPath = useTs ? tsPath : jsPath;

    this.worker = new Worker(workerPath, {
      // Bootstrap swc-node so the worker can require the .ts file directly
      // (matches how `npm start` runs the main process).
      execArgv: useTs ? ["-r", "@swc-node/register"] : [],
    });

    this.worker.on("message", (msg: any) => {
      const slot = this.pending.get(msg?.id);
      if (!slot) return;
      this.pending.delete(msg.id);
      if (msg.error) slot.reject(new Error(msg.error));
      else slot.resolve(msg);
    });

    this.worker.on("error", (err) => {
      // Fatal worker-level error — reject everything pending so the loop
      // doesn't hang.
      for (const [, slot] of this.pending) slot.reject(err);
      this.pending.clear();
      logger.error(`${logPrefix} archive-anonymizer worker errored: ${err}`);
    });
  }

  call(params: {
    compressed_data: Buffer;
    accountId: string;
    oldUsername: string | null;
    anonymizedName: string;
  }): Promise<{ changed: boolean; compressed_data?: Buffer }> {
    if (this.terminated) return Promise.reject(new Error("worker terminated"));
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.worker.postMessage({ id, ...params });
    });
  }

  async terminate(): Promise<void> {
    if (this.terminated) return;
    this.terminated = true;
    await this.worker.terminate();
  }
}

/**
 * Anonymize occurrences of accountId / username inside a single MatchArchive
 * entry. The blob is zstd-compressed JSON; the worker decompresses, walks,
 * replaces, and recompresses. Main thread only does the Mongo save when a
 * change actually happened.
 */
async function anonymizeArchive(
  worker: ArchiveAnonymizerWorker,
  doc: any,
  accountId: string,
  oldUsername: string | null,
  anonymizedName: string,
): Promise<boolean> {
  if (!doc?.compressed_data || !Buffer.isBuffer(doc.compressed_data)) return false;

  const result = await worker.call({
    compressed_data: doc.compressed_data,
    accountId,
    oldUsername,
    anonymizedName,
  });
  if (!result.changed || !result.compressed_data) return false;

  doc.compressed_data = Buffer.isBuffer(result.compressed_data)
    ? result.compressed_data
    : Buffer.from(result.compressed_data as any);
  await doc.save();
  return true;
}

/**
 * Run the actual deletion for a player. Idempotent — safe to re-run if
 * a previous attempt died partway.
 */
export async function processDeletion(
  accountId: string,
  processedBy: string,
): Promise<DeletionReport> {
  const report: DeletionReport = {
    accountId,
    anonymizedDisplayName: makeAnonymizedName(),
    mongo: {},
    redisKeysDeleted: 0,
    archivesAnonymized: 0,
    archiveFailures: 0,
    errors: [],
  };

  logger.info(`${logPrefix} Beginning deletion for account ${accountId} (processedBy=${processedBy})`);

  // ── Capture the username BEFORE we wipe playertesters; needed for archive
  //    anonymization since some archive blobs reference players by name.
  let oldUsername: string | null = null;
  try {
    const existing = await PlayerTesterModel.findById(new Types.ObjectId(accountId)).lean();
    oldUsername = existing?.name ?? existing?.hydraUsername ?? null;
  } catch (e) {
    report.errors.push(`Failed to read pre-existing playertester: ${e}`);
  }

  // ── 1. Delete the account record itself
  try {
    const r = await PlayerTesterModel.deleteOne({ _id: new Types.ObjectId(accountId) });
    report.mongo.playertesters = r.deletedCount ?? 0;
  } catch (e) {
    report.errors.push(`playertesters: ${e}`);
  }

  // ── 2. Delete derived per-account collections
  for (const [name, op] of [
    ["eloratings", () => EloRatingModel.deleteMany({ account_id: accountId })],
    ["playerstats", () => PlayerStatsModel.deleteMany({ account_id: accountId })],
    ["cosmetics", () => CosmeticsModel.deleteMany({ account_id: accountId })],
    ["friendlists_self", () => FriendListModel.deleteOne({ accountId })],
  ] as const) {
    try {
      const r = await op();
      report.mongo[name] = r.deletedCount ?? 0;
    } catch (e) {
      report.errors.push(`${name}: ${e}`);
    }
  }

  // ── 3. Pull this account ID out of OTHER users' friend lists
  try {
    const r = await FriendListModel.updateMany(
      { "friends.friendAccountId": accountId },
      { $pull: { friends: { friendAccountId: accountId } } },
    );
    report.mongo.friendlists_pulled_from_others = r.modifiedCount ?? 0;
  } catch (e) {
    report.errors.push(`friendlists_pulled_from_others: ${e}`);
  }

  // ── 4. Anonymize match archive references (off the main thread)
  //
  // Spin up a single worker_threads worker for the duration of this
  // deletion. The worker handles all zstd decompress/walk/recompress; the
  // main thread does only the Mongo find + save and the queue plumbing.
  // This keeps the event loop responsive for live matches and HTTP traffic
  // while a heavy deletion is running.
  let archiveWorker: ArchiveAnonymizerWorker | null = null;
  try {
    archiveWorker = new ArchiveAnonymizerWorker();

    // Find archives that mention this accountId in their compressed payload.
    // We can't query inside the compressed blob — fetch all and check, or
    // narrow by a sidecar field if one exists. The current MatchArchive only
    // has match_id/timestamp/compressed_data, so we fetch the recent N (last
    // 12 months) and inspect each.
    const cutoff = new Date(Date.now() - 365 * 24 * 60 * 60 * 1000);
    const archives = await MatchArchiveModel.find({ timestamp: { $gte: cutoff } });
    for (const doc of archives) {
      try {
        const ok = await anonymizeArchive(
          archiveWorker,
          doc,
          accountId,
          oldUsername,
          report.anonymizedDisplayName,
        );
        if (ok) report.archivesAnonymized++;
      } catch (e) {
        report.archiveFailures++;
        logger.warn(`${logPrefix} Failed to anonymize archive ${doc.match_id}: ${e}`);
      }
    }
  } catch (e) {
    report.errors.push(`match_archives: ${e}`);
  } finally {
    if (archiveWorker) {
      try { await archiveWorker.terminate(); } catch {}
    }
  }

  // ── 5. Wipe Redis keys
  try {
    let total = 0;
    // All patterns must include `accountId` — never use a global wildcard
    // here, doing so would clobber state for unrelated active players.
    // (`match_to_set:*` is intentionally NOT wiped: those keys are not
    // keyed by accountId and have a short TTL; they age out naturally.)
    for (const pattern of [
      `connections:${accountId}`,
      `connections:${accountId}:*`,
      `player:${accountId}:*`,
      `player_lobby:${accountId}`,
      `player_ranked_set:${accountId}`,
      `ranked_disconnect:${accountId}`,
      `bot_config:${accountId}`,
      `pending_join_lobby:${accountId}`,
      `fun_fact_pending:${accountId}`,
      `ssc_custom_lobby_player:${accountId}`,
    ]) {
      if (pattern.endsWith("*")) {
        total += await deleteRedisKeysByPattern(pattern);
      } else {
        const n = await redisClient.del(pattern);
        total += typeof n === "number" ? n : 0;
      }
    }
    report.redisKeysDeleted = total;
  } catch (e) {
    report.errors.push(`redis: ${e}`);
  }

  logger.info(
    `${logPrefix} Deletion complete for ${accountId}: ${JSON.stringify({
      mongo: report.mongo,
      redis: report.redisKeysDeleted,
      archives: report.archivesAnonymized,
      failures: report.archiveFailures,
      errors: report.errors.length,
    })}`,
  );

  return report;
}
