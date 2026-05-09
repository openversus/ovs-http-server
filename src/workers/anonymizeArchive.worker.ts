// Worker thread for match-archive anonymization.
//
// Decompressing/recompressing zstd blobs is CPU-bound and blocks the Node
// main event loop in zstd-napi (its decompress/compress are synchronous
// native calls). For a heavy player with hundreds or thousands of
// archived matches, doing this on the main thread would freeze the
// server for tens of seconds — long enough to break active matches and
// time out reverse-proxy connections.
//
// This worker isolates those CPU-bound ops so the main thread can keep
// serving HTTP / matchmaking ticks / WS messages while a deletion runs.
//
// Protocol:
//   main → worker: { id, compressed_data, accountId, oldUsername, anonymizedName }
//   worker → main: { id, changed, compressed_data? } | { id, error }

import { parentPort } from "node:worker_threads";
import * as zstd from "zstd-napi";

if (!parentPort) {
  throw new Error("anonymizeArchive.worker must be loaded via worker_threads");
}

interface InMessage {
  id: number;
  compressed_data: Buffer | Uint8Array | { type: "Buffer"; data: number[] };
  accountId: string;
  oldUsername: string | null;
  anonymizedName: string;
}

interface OutMessage {
  id: number;
  changed: boolean;
  compressed_data?: Buffer;
  error?: string;
}

function toBuffer(input: InMessage["compressed_data"]): Buffer {
  if (Buffer.isBuffer(input)) return input;
  if (input instanceof Uint8Array) return Buffer.from(input);
  // Some serialization paths surface buffers as { type: "Buffer", data: [...] }
  if (input && typeof input === "object" && "data" in input && Array.isArray((input as any).data)) {
    return Buffer.from((input as any).data);
  }
  return Buffer.from(input as any);
}

parentPort.on("message", (msg: InMessage) => {
  let id = msg?.id ?? -1;
  try {
    const { compressed_data, accountId, oldUsername, anonymizedName } = msg;
    if (!compressed_data) {
      const out: OutMessage = { id, changed: false };
      parentPort!.postMessage(out);
      return;
    }

    const buf = toBuffer(compressed_data);
    const raw = zstd.decompress(buf);

    let json: any;
    try {
      json = JSON.parse(raw.toString("utf8"));
    } catch {
      const out: OutMessage = { id, changed: false };
      parentPort!.postMessage(out);
      return;
    }

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

    if (!changed) {
      const out: OutMessage = { id, changed: false };
      parentPort!.postMessage(out);
      return;
    }

    const recompressed = zstd.compress(
      Buffer.from(JSON.stringify(json), "utf8"),
      { compressionLevel: 9 },
    );
    const out: OutMessage = { id, changed: true, compressed_data: recompressed };
    // Transfer the buffer so we don't pay a copy cost on the way back.
    parentPort!.postMessage(out, [recompressed.buffer as ArrayBuffer]);
  } catch (e: any) {
    const out: OutMessage = { id, changed: false, error: String(e?.stack || e) };
    parentPort!.postMessage(out);
  }
});
