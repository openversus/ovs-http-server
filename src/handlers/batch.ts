import express, { Request, Response } from "express";
import { logger } from "../config/logger";

import { handle_batch_custom } from "../middleware/batchMiddleware";

export async function handleBatch(req: Request<{}, {}, {}, {}>, res: Response) {
  const requests = (req.body as any)?.requests;
  if (requests && Array.isArray(requests)) {
    const urls = requests.map((r: any) => `${r.method || 'GET'} ${r.relative_url || r.url || '?'}`);
    logger.info(`[Batch]: Received batch with ${requests.length} requests:\n  ${urls.join('\n  ')}`);
  }
  await handle_batch_custom(req, res);
}
