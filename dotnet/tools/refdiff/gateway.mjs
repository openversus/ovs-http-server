// Fake games on the websocket: each connects the way the game does (the init frame carrying its session token), answers
// the ping, and keeps every message the server sends it, decoded. A harness compares what each player was sent, so the
// TS websocket can deliver for both servers under test: the TS server's own channels, and the C# port's ws:send.
// Run from the repository root (it loads the TS server's node_modules).
import { require } from "./refdiff.mjs";

const WebSocket = require(process.cwd() + "/node_modules/ws");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const PING = 0x0c;
const PONG = 0x0a;

/**
 * The game's first frame as the TS server reads it (parseInitHydraWebsocketMessage): 0x13 bytes the server skips, the
 * token's length (u16, big-endian), the token, a 12-byte id, then a Hydra map.
 */
export function initFrame(token, n = 1) {
  const jwt = Buffer.from(token, "utf8");
  const length = Buffer.alloc(2);
  length.writeUInt16BE(jwt.length);
  const id = Buffer.alloc(12, n);
  const map = Buffer.from(new HydraEncoder(true).encodeValue({ connection: {} }) ?? []);
  return Buffer.concat([Buffer.alloc(0x13), length, jwt, id, map]);
}

/** Decodes one server frame; the id frame, pings and anything that does not decode are kept as hex. */
export function decodeFrame(bytes) {
  if (bytes.length === 1) return { raw: bytes.toString("hex") };
  try {
    return new HydraDecoder(bytes).readValue();
  } catch {
    return { raw: bytes.toString("hex") };
  }
}

/**
 * Connects one fake game per player ({id, token}) to `url`; resolves when every one has had the server's id frame.
 * `frames(id)` is what that player has been sent since the last `clear()`, pings left out.
 */
export async function connectPlayers(url, players) {
  const received = new Map(players.map((p) => [p.id, []]));
  const sockets = new Map();
  const open = (player, i) => new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    sockets.set(player.id, ws);
    let greeted = false;
    const timer = setTimeout(() => reject(new Error(`no id frame for ${player.id}`)), 5000);
    ws.on("open", () => ws.send(initFrame(player.token, i + 1)));
    ws.on("message", (data) => {
      const bytes = Buffer.from(data);
      if (bytes.length === 1 && bytes[0] === PING) {
        ws.send(Buffer.from([PONG]));
        return;
      }
      if (!greeted) {
        greeted = true;
        clearTimeout(timer);
        resolve();
        return;
      }
      received.get(player.id).push(decodeFrame(bytes));
    });
    ws.on("error", reject);
  });
  await Promise.all(players.map(open));
  return {
    frames: (id) => received.get(id),
    all: () => Object.fromEntries([...received].map(([id, list]) => [id, list])),
    clear: () => { for (const list of received.values()) list.length = 0; },
    close: () => { for (const ws of sockets.values()) ws.terminate(); },
    /** Closes these players' games (as a game that went away: no close frame); resolves once each socket is closed. */
    async drop(ids) {
      await Promise.all(ids.map((id) => new Promise((resolve) => {
        const ws = sockets.get(id);
        if (!ws || ws.readyState === WebSocket.CLOSED) return resolve();
        ws.once("close", resolve);
        ws.terminate();
      })));
    },
    /** The players whose socket the server closed. */
    closed: () => players.filter((p) => sockets.get(p.id).readyState === WebSocket.CLOSED).map((p) => p.id),
    /** Connects again every player whose socket the server closed; their ids. */
    async reopen() {
      const gone = players.map((p, i) => [p, i]).filter(([p]) => sockets.get(p.id).readyState === WebSocket.CLOSED);
      await Promise.all(gone.map(([p, i]) => open(p, i)));
      return gone.map(([p]) => p.id);
    },
  };
}
