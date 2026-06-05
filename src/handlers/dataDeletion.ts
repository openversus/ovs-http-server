// Data deletion request handlers.
//
// Public flow:
//   POST   /api/data-deletion-request           submit (rate-limited, honeypot)
//   GET    /api/data-deletion-request/:id       status
//   POST   /api/data-deletion-request/:id/cancel cancel a still-pending request
//
// Admin flow (gated by /admin auth middleware in server.ts):
//   GET    /api/admin/data-deletion-requests             list
//   POST   /api/admin/data-deletion-requests/:id/match   admin sets matchedAccountId + notes
//   POST   /api/admin/data-deletion-requests/:id/process run actual deletion
//   POST   /api/admin/data-deletion-requests/:id/reject  mark rejected with notes
//
// On submission a Discord webhook is fired so admins see new requests in
// near-real-time. Webhook URL comes from env DISCORD_DATA_REQUEST_WEBHOOK_URL.

import { Request, Response } from "express";
import { Types } from "mongoose";
import { logger } from "../config/logger";
import { redisClient } from "../config/redis";
import { tryGetRealIP } from "../middleware/auth";
import env from "../env/env";
import {
  DataDeletionRequestModel,
  type DataDeletionStatus,
} from "../database/DataDeletionRequest";
import { processDeletion } from "../services/dataDeletionService";
import { PlayerTesterModel } from "../database/PlayerTester";

const serviceName = "Handlers.DataDeletion";
const logPrefix = `[${serviceName}]:`;

// ── Anti-spam ──
//
// Per-IP rate limit: max 3 requests per IP per 24h, regardless of status.
// Completed and rejected requests still count — this is intentional, not a
// bug. If we filtered to status: "pending" only, a user could file → admin
// approves & processes → file again → process → file again... unbounded
// noise per day. Counting all requests in the window caps IP-level abuse
// while still allowing the rare legitimate refile (the user just waits).
// Backed by Mongo countDocuments (not Redis, despite an earlier comment
// claim) — at this endpoint's traffic the Mongo query is microseconds.
async function checkRateLimit(ip: string): Promise<{ ok: boolean; existingCount: number }> {
  const dayAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);
  const count = await DataDeletionRequestModel.countDocuments({
    requestIp: ip,
    createdAt: { $gte: dayAgo },
  });
  return { ok: count < 3, existingCount: count };
}

// ── Discord notify ──
async function notifyDiscord(req: any): Promise<void> {
  const url = env.DISCORD_DATA_REQUEST_WEBHOOK_URL;
  if (!url) {
    // info, not warn — running without a webhook is a valid configuration
    // (some operators don't want Discord pings for deletion requests).
    // Reserves the WARN level for actually-actionable problems.
    logger.info(`${logPrefix} DISCORD_DATA_REQUEST_WEBHOOK_URL not set; skipping notification`);
    return;
  }
  try {
    await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        // Intentionally don't include the requester's IP here — the channel
        // may have admins beyond the immediate operators. Full details
        // (IP, user-agent) are visible only inside the admin review UI.
        content:
          `🗑️ **New data deletion request**\n` +
          `**Name:** \`${req.requestedName}\`\n` +
          `**Reason:** ${req.reason ? "```\n" + String(req.reason).slice(0, 400) + "\n```" : "_(none)_"}\n` +
          `**Request ID:** \`${req._id}\`\n` +
          `Review at <{ADMIN_REVIEW_URL}>/admin/data-deletion-requests`.replace("{ADMIN_REVIEW_URL}", env.GAME_DOMAIN ? `https://${env.GAME_DOMAIN}` : ""),
      }),
    });
  } catch (e) {
    logger.error(`${logPrefix} Discord webhook post failed: ${e}`);
  }
}

// ── Public: submit ──
export async function handleSubmit(req: Request, res: Response) {
  try {
    const body = (req.body ?? {}) as any;

    // Honeypot: a hidden form field bots fill in. Any non-empty value here =
    // bot. Silently accept (200 OK) so the bot thinks it succeeded but
    // don't actually create a record.
    if (body.website && String(body.website).trim() !== "") {
      logger.info(`${logPrefix} Honeypot tripped on submission; silently dropping`);
      res.status(200).json({ ok: true, requestId: "honeypot" });
      return;
    }

    const requestedName = String(body.requestedName ?? "").trim();
    const reason = String(body.reason ?? "").trim().slice(0, 1000);
    if (!requestedName) {
      res.status(400).json({ error: "requestedName is required" });
      return;
    }
    if (requestedName.length > 64) {
      res.status(400).json({ error: "requestedName too long (max 64)" });
      return;
    }

    const ip = tryGetRealIP(req) || (req.ip ?? "unknown");
    const ua = String(req.headers["user-agent"] || "").slice(0, 256);

    // Rate-limit by IP
    const rl = await checkRateLimit(ip);
    if (!rl.ok) {
      logger.warn(`${logPrefix} Rate limit hit (already ${rl.existingCount} requests in 24h from this IP)`);
      res.status(429).json({
        error: "too_many_requests",
        message: "You already have multiple deletion requests filed in the last 24 hours. Please wait or contact us via Discord.",
      });
      return;
    }

    const created = await DataDeletionRequestModel.create({
      requestedName,
      reason,
      requestIp: ip,
      requestUserAgent: ua,
      status: "pending",
    });

    // Don't log the requesting IP — it's PII. The IP is persisted on the
    // request record itself and visible to admins via the review UI.
    logger.info(`${logPrefix} Created deletion request ${created._id} for name="${requestedName}"`);

    // Fire Discord notification (don't await — non-critical)
    notifyDiscord(created.toObject()).catch(() => {});

    res.status(201).json({
      ok: true,
      requestId: created._id.toString(),
      status: "pending",
      message: "Your deletion request has been received. An admin will review it shortly. To cancel, keep this request ID.",
    });
  } catch (e) {
    logger.error(`${logPrefix} handleSubmit error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

// ── Public: status ──
export async function handleStatus(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const doc = await DataDeletionRequestModel.findById(id).lean();
    if (!doc) {
      res.status(404).json({ error: "not_found" });
      return;
    }
    // Don't leak admin notes / matched account ID / IP to the public.
    res.json({
      requestId: String(doc._id),
      status: doc.status,
      requestedName: doc.requestedName,
      createdAt: (doc as any).createdAt,
      processedAt: doc.processedAt ?? null,
    });
  } catch (e) {
    logger.error(`${logPrefix} handleStatus error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

// ── Public: cancel ──
export async function handleCancel(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const doc = await DataDeletionRequestModel.findById(id);
    if (!doc) {
      res.status(404).json({ error: "not_found" });
      return;
    }
    if (doc.status !== "pending") {
      res.status(409).json({
        error: "not_pending",
        message: `Request is already ${doc.status} and cannot be cancelled.`,
      });
      return;
    }
    doc.status = "cancelled";
    await doc.save();
    logger.info(`${logPrefix} Request ${id} cancelled by user`);
    res.json({ ok: true, status: "cancelled" });
  } catch (e) {
    logger.error(`${logPrefix} handleCancel error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

// ── Admin: list ──
export async function handleAdminList(req: Request, res: Response) {
  try {
    const status = (req.query.status as string) || "";
    const filter: any = {};
    if (status) filter.status = status as DataDeletionStatus;
    const docs = await DataDeletionRequestModel.find(filter).sort({ createdAt: -1 }).limit(100).lean();
    res.json({
      requests: docs.map((d) => ({
        requestId: String(d._id),
        requestedName: d.requestedName,
        reason: d.reason,
        requestIp: d.requestIp,
        requestUserAgent: d.requestUserAgent,
        status: d.status,
        matchedAccountId: d.matchedAccountId,
        adminNotes: d.adminNotes,
        processedBy: d.processedBy,
        processedAt: d.processedAt ?? null,
        createdAt: (d as any).createdAt,
      })),
    });
  } catch (e) {
    logger.error(`${logPrefix} handleAdminList error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

// ── Admin: find candidate playertester accounts for a deletion request ──
//
// Looks up potential accounts by:
//   1. Exact in-game name match (case-insensitive)
//   2. Same IP as the request originated from
// Returns up to 10 unique candidates with the fields admin needs to
// cross-check identity (steamId / epicId / hardwareId presence indicates
// a real registered account; multiple matches with the same name need IP
// disambiguation).
export async function handleAdminCandidates(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const requestDoc = await DataDeletionRequestModel.findById(id).lean();
    if (!requestDoc) {
      res.status(404).json({ error: "not_found" });
      return;
    }

    const nameRegex = new RegExp(`^${escapeRegex(requestDoc.requestedName)}$`, "i");
    // Two parallel finds — by name and by IP — then dedupe by _id.
    const [byName, byIp] = await Promise.all([
      PlayerTesterModel.find({ name: nameRegex })
        .select("_id name ip steamId epicId hardwareId hydraUsername character")
        .limit(10)
        .lean(),
      PlayerTesterModel.find({ ip: requestDoc.requestIp })
        .select("_id name ip steamId epicId hardwareId hydraUsername character")
        .limit(10)
        .lean(),
    ]);

    const seen = new Set<string>();
    const candidates: any[] = [];
    for (const c of [...byName, ...byIp]) {
      const idStr = String(c._id);
      if (seen.has(idStr)) continue;
      seen.add(idStr);
      const matchedBy: string[] = [];
      if (byName.find((x) => String(x._id) === idStr)) matchedBy.push("name");
      if (byIp.find((x) => String(x._id) === idStr)) matchedBy.push("ip");
      candidates.push({
        _id: idStr,
        name: c.name || "",
        hydraUsername: c.hydraUsername || "",
        ip: c.ip || "",
        ipMatchesRequest: c.ip === requestDoc.requestIp,
        hasSteamId: !!c.steamId,
        hasEpicId: !!c.epicId,
        hasHardwareId: !!c.hardwareId,
        character: c.character || "",
        matchedBy,
      });
    }

    res.json({
      requestId: id,
      requestedName: requestDoc.requestedName,
      requestIp: requestDoc.requestIp,
      candidates,
    });
  } catch (e) {
    logger.error(`${logPrefix} handleAdminCandidates error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// ── Admin: set matched account id + notes (without processing) ──
export async function handleAdminMatch(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const body = (req.body ?? {}) as any;
    const matchedAccountId = String(body.matchedAccountId ?? "").trim();
    const adminNotes = String(body.adminNotes ?? "").trim().slice(0, 2000);

    if (matchedAccountId && !Types.ObjectId.isValid(matchedAccountId)) {
      res.status(400).json({ error: "invalid_matched_account_id" });
      return;
    }

    const doc = await DataDeletionRequestModel.findByIdAndUpdate(
      id,
      { $set: { matchedAccountId, adminNotes } },
      { new: true, lean: true },
    );
    if (!doc) {
      res.status(404).json({ error: "not_found" });
      return;
    }
    res.json({ ok: true });
  } catch (e) {
    logger.error(`${logPrefix} handleAdminMatch error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}

// ── Admin: process (actually delete the data) ──
export async function handleAdminProcess(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const body = (req.body ?? {}) as any;
    const processedBy = String(body.processedBy ?? "admin").trim().slice(0, 64);

    const doc = await DataDeletionRequestModel.findById(id);
    if (!doc) {
      res.status(404).json({ error: "not_found" });
      return;
    }
    if (doc.status !== "pending") {
      res.status(409).json({ error: "not_pending", currentStatus: doc.status });
      return;
    }
    if (!doc.matchedAccountId) {
      res.status(400).json({
        error: "no_matched_account",
        message: "Set matchedAccountId via /api/admin/data-deletion-requests/:id/match before processing.",
      });
      return;
    }

    logger.info(`${logPrefix} Admin ${processedBy} processing deletion request ${id} → account ${doc.matchedAccountId}`);
    const report = await processDeletion(doc.matchedAccountId, processedBy);

    doc.status = "processed";
    doc.processedBy = processedBy;
    doc.processedAt = new Date();
    doc.adminNotes = (doc.adminNotes ? doc.adminNotes + "\n\n" : "") +
      `[processed] ${JSON.stringify(report)}`;
    await doc.save();

    res.json({ ok: true, report });
  } catch (e) {
    logger.error(`${logPrefix} handleAdminProcess error: ${e}`);
    res.status(500).json({ error: "internal_error", message: String(e) });
  }
}

// ── Admin: reject ──
export async function handleAdminReject(req: Request, res: Response) {
  try {
    const id = req.params.id;
    if (!Types.ObjectId.isValid(id)) {
      res.status(400).json({ error: "invalid_id" });
      return;
    }
    const body = (req.body ?? {}) as any;
    const adminNotes = String(body.adminNotes ?? "").trim().slice(0, 2000);
    const processedBy = String(body.processedBy ?? "admin").trim().slice(0, 64);

    const doc = await DataDeletionRequestModel.findById(id);
    if (!doc) {
      res.status(404).json({ error: "not_found" });
      return;
    }
    if (doc.status !== "pending") {
      res.status(409).json({ error: "not_pending", currentStatus: doc.status });
      return;
    }

    doc.status = "rejected";
    doc.processedBy = processedBy;
    doc.processedAt = new Date();
    if (adminNotes) doc.adminNotes = (doc.adminNotes ? doc.adminNotes + "\n\n" : "") + `[rejected] ${adminNotes}`;
    await doc.save();

    res.json({ ok: true });
  } catch (e) {
    logger.error(`${logPrefix} handleAdminReject error: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
}
