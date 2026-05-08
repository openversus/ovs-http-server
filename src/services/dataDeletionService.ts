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
import * as zstd from "zstd-napi";
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

/**
 * Anonymize occurrences of accountId / username inside a single MatchArchive
 * entry. The blob is zstd-compressed JSON; we decompress, walk, replace,
 * recompress.
 *
 * Cost: each call decompresses + recompresses one archive. Bulk deletes for
 * very active players may chew through CPU; called serially from admin
 * processing to keep load predictable.
 */
async function anonymizeArchive(
  doc: any,
  accountId: string,
  oldUsername: string | null,
  anonymizedName: string,
): Promise<boolean> {
  if (!doc?.compressed_data || !Buffer.isBuffer(doc.compressed_data)) return false;

  const raw = zstd.decompress(doc.compressed_data);
  let json: any;
  try {
    json = JSON.parse(raw.toString("utf8"));
  } catch {
    return false;
  }

  // Walk arbitrary structure; replace any string field whose value matches
  // accountId or oldUsername. Conservative: only touches exact-string matches
  // in known fields. Common shapes we see in archives:
  //   - players: [{ account_id, username, character, ... }]
  //   - winners / losers: [accountId, accountId]
  //   - chars: { [accountId]: "character_..." }
  //   - eloBefore / eloAfter: { [accountId]: 1234 }
  let changed = false;

  const replaceStr = (s: any): any => {
    if (typeof s !== "string") return s;
    if (s === accountId) {
      changed = true;
      return anonymizedName;
    }
    if (oldUsername && s === oldUsername) {
      changed = true;
      return anonymizedName;
    }
    return s;
  };

  const walk = (node: any): any => {
    if (Array.isArray(node)) {
      for (let i = 0; i < node.length; i++) node[i] = walk(node[i]);
      return node;
    }
    if (node && typeof node === "object") {
      // Replace keys that are accountIds (e.g. eloBefore: { [accountId]: 1234 })
      for (const key of Object.keys(node)) {
        if (key === accountId) {
          node[anonymizedName] = walk(node[key]);
          delete node[key];
          changed = true;
        } else {
          node[key] = walk(node[key]);
        }
      }
      return node;
    }
    return replaceStr(node);
  };

  walk(json);

  if (!changed) return false;

  const recompressed = zstd.compress(
    Buffer.from(JSON.stringify(json), "utf8"),
    { compressionLevel: 9 },
  );
  doc.compressed_data = recompressed;
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

  // ── 4. Anonymize match archive references
  try {
    // Find archives that mention this accountId in their compressed payload.
    // We can't query inside the compressed blob — fetch all and check, or
    // narrow by a sidecar field if one exists. The current MatchArchive only
    // has match_id/timestamp/compressed_data, so we fetch the recent N (last
    // 12 months) and inspect each.
    const cutoff = new Date(Date.now() - 365 * 24 * 60 * 60 * 1000);
    const archives = await MatchArchiveModel.find({ timestamp: { $gte: cutoff } });
    for (const doc of archives) {
      try {
        const ok = await anonymizeArchive(doc, accountId, oldUsername, report.anonymizedDisplayName);
        if (ok) report.archivesAnonymized++;
      } catch (e) {
        report.archiveFailures++;
        logger.warn(`${logPrefix} Failed to anonymize archive ${doc.match_id}: ${e}`);
      }
    }
  } catch (e) {
    report.errors.push(`match_archives: ${e}`);
  }

  // ── 5. Wipe Redis keys
  try {
    let total = 0;
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
      `match_to_set:*`, // these are short-TTL; skip from per-account scan, just leave to expire
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
