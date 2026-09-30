// PUT /batch on the TS server and the C# port, compared item by item and byte by byte. The C# port answers the
// sub-requests it has ported and sends the rest to the TS server as one TS batch (Batch:TsUrl must be <tsUrl>), so
// both servers run against the same stores. Run from the repository root:
//
//   REF_MONGO_URI=mongodb://127.0.0.1:27017/<db> REF_JWT_SECRET=<both servers' JWT_SECRET> REF_CS_CONTROL=<C# control port> \
//     [REF_ACCOUNT=<account id>] node dotnet/tools/batch/batch_diff.mjs run <tsUrl> <csUrl> [accounts]
//
// Batches: every distinct batch the game sent in the captures (REF_CORPUS, default dotnet/local/hydra-corpus; account
// ids rewritten), and one of the ported reads that could come in a batch plus sub-requests C# must send on (a stubbed
// id, an unknown path, an unported SSC function), for the first <accounts> players with an identity (default 25),
// plus REF_ACCOUNT (an account id) if set. Each batch runs in two C# modes, set live through the control API: "mixed"
// (Batch:ForwardRoutes empty, so C# answers what it has ported) and "all-ts" (every game route in the route map listed, so
// everything goes to the TS server).
//
// Each batch is sent to the TS server, then to C#, then to the TS server again. A path whose value differs between the
// two TS answers changes on every request (a timestamp, a random id) and is set aside, and listed; anything else C#
// answers differently is a difference. Where nothing was set aside, the whole answer must be the same bytes.
//
// Except: in "mixed" mode C# answers /matches/all and the username search itself, and corrects the TS answer on purpose
// (MatchHistoryService, ProfilesService.SearchAsync; matches_diff.mjs and search_diff.mjs check the corrections), so
// those items must instead equal the same request answered by C# alone. The same check covers what the TS server
// cannot answer inside a batch at all, sent to C# only: /file_storage (its TS handler reads req.protocol, which throws
// on the TS batch's copied request, and the batch never answers) and the leaderboard views (their TS handlers call
// res.setHeader, which the TS batch's fake response lacks; the throw is an unhandled rejection). Nested batches and malformed items are never sent to the TS server; the http tests cover them.
import fs from "fs";
import { require } from "../refdiff/refdiff.mjs";

const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const [, , command, tsUrl, csUrl, limit] = process.argv;
const control = process.env.REF_CS_CONTROL;
if (command !== "run" || !tsUrl || !csUrl || !control || !process.env.REF_MONGO_URI || !process.env.REF_JWT_SECRET) {
  console.error("usage: REF_MONGO_URI=... REF_JWT_SECRET=... REF_CS_CONTROL=<port> batch_diff.mjs run <tsUrl> <csUrl> [accounts]");
  process.exit(2);
}

const HYDRA = "application/x-ag-binary";
const corpus = process.env.REF_CORPUS ?? "dotnet/local/hydra-corpus";
const ID = /[0-9a-f]{24}/g;

// The captured batches, one of each distinct shape once account ids are replaced.
const shapes = new Map();
for (const file of fs.readdirSync(`${corpus}/req`).filter(f => f.endsWith("_batch.bin")).sort()) {
  const batch = new HydraDecoder(fs.readFileSync(`${corpus}/req/${file}`)).readValue();
  const key = JSON.stringify(batch).replace(ID, "{id}");
  if (!shapes.has(key)) shapes.set(key, { name: `captured ${file}`, text: key });
}

const mongo = new MongoClient(process.env.REF_MONGO_URI);
await mongo.connect();
// Players with an identity to log in with (a login without one makes a ghost account instead).
const withIdentity = { $or: ["steamId", "epicId", "installId"].map(f => ({ [f]: { $nin: [null, ""] } })) };
const players = await mongo.db().collection("playertesters").find(withIdentity).sort({ _id: 1 }).limit(Number(limit ?? 25)).toArray();
if (process.env.REF_ACCOUNT) {
  const extra = await mongo.db().collection("playertesters").findOne({ _id: new (require(process.cwd() + "/node_modules/mongodb").ObjectId)(process.env.REF_ACCOUNT) });
  if (extra && !players.some(p => p._id.equals(extra._id))) players.push(extra);
}
await mongo.close();

// The SSC handlers find the player through the connection record /access writes (resolveAccountFromRequest), so each
// player logs in first, on the TS server, as the game does: the identity token the client mod sends (the player's own
// stored ids, so /access finds that player) and the captured /access body without its Steam ticket (never read). Each
// player gets an address of its own from 198.18.0.0/15 (benchmarking range), so no two share an IP-keyed record, and
// the batches use the session token /access answers with. The login writes what a real one writes (lastSeenAt, ip,
// connections:{id}, ...) into these players' records.
const accessFile = fs.readdirSync(`${corpus}/req`).find(f => f.endsWith("_access.bin"));
const accessBody = new HydraDecoder(fs.readFileSync(`${corpus}/req/${accessFile}`)).readValue();
delete accessBody.auth?.steam;

async function login(player, index) {
  const identity = jwt.sign({
    steamId: player.steamId ?? "", epicId: player.epicId ?? "", installId: player.installId ?? "",
    hardwareId: player.hardwareId ?? "", clientVersion: player.clientVersion ?? "", identityRegistered: "1",
  }, process.env.REF_JWT_SECRET);
  const ip = `198.18.${index >> 8}.${(index & 255) + 1}`;
  const encoder = new HydraEncoder();
  encoder.encodeValue(accessBody);
  const response = await fetch(`${tsUrl}/access`, {
    method: "POST", body: encoder.returnValue(), signal: AbortSignal.timeout(30000),
    headers: { "content-type": HYDRA, "x-hydra-access-token": identity, "x-real-ip": ip },
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const answer = response.ok && bytes.length ? new HydraDecoder(bytes).readValue() : null;
  const claims = typeof answer?.token === "string" ? jwt.decode(answer.token) : null;
  return { ip, token: answer?.token, id: claims?.id, status: response.status };
}

// The ported reads that may come in a batch, and sub-requests C# has to send on.
function synthetic(id, name) {
  const get = url => ({ verb: "GET", url, headers: {} });
  return {
    options: { allow_failures: true, parallel: true },
    requests: [
      get("/friends/me"),
      get("/friends/me/invitations/incoming"),
      get("/friends/me/invitations/outgoing"),
      get("/social/me/blocked"),
      get("/commerce/products?partial_response=1"),
      get("/commerce/products"),
      get("/commerce/purchases/me"),
      get("/commerce/steam/mtx_user_info/me"),
      get(`/profiles/search_queries/get-by-username/run?username=${encodeURIComponent(name.slice(0, 3))}`),
      get(`/matches/all/${id}?count=3&page=1&fields=server_data`),
      { verb: "PUT", url: `/profiles/${id}/inventory`, headers: { "x-hydra-http-method": "GET" }, body: { advanced_filter: { boolean: "any", subfilters: [], tags: ["currency"] }, count: 1000, page: 1 } },
      { verb: "PUT", url: "/profiles/bulk", headers: { "x-hydra-http-method": "GET" }, body: { ids: [id], fields: ["presence"] } },
      get("/commerce/purchases/0000000000000000000a0001"),
      get("/no/such/route"),
      get("/ssc/invoke/get_country_code"),
    ],
  };
}

// Every game route in the route map (docs/routes.json), so "all-ts" stays all as routes are ported. (The map writes "?"
// for a method nobody has seen, and a regex path for the TS /.*/access route; neither can be listed.)
const ALL_TS = JSON.parse(fs.readFileSync("dotnet/docs/routes.json", "utf8"))
  .filter((r) => r.kind === "game" && ["GET", "PUT", "POST", "DELETE"].includes(r.method) && !r.path.startsWith("/.*"))
  .map((r) => `${r.method} ${r.path}`).join(", ");

async function setting(key, value) {
  const response = await fetch(`http://127.0.0.1:${control}/control/settings/${key}?scope=instance`, { method: "PUT", body: value });
  if (!response.ok) throw new Error(`setting ${key}: ${response.status} ${await response.text()}`);
}
const setForwardRoutes = value => setting("Batch:ForwardRoutes", value);

// The TS server never answers a batch in which one sub-request throws (get_equipped_cosmetics does when the player has
// no connection record): such a batch is recorded as a hang, and C# must still answer it, with 504 for what it sent there.
const TS_WAIT = 15000, FORWARD_TIMEOUT = 5;
async function send(base, batch, tok, ip, wait = 60000) {
  const encoder = new HydraEncoder();
  encoder.encodeValue(batch);
  const response = await fetch(`${base}/batch`, {
    method: "PUT", body: encoder.returnValue(),
    headers: { "content-type": HYDRA, "x-hydra-access-token": tok, "x-real-ip": ip }, signal: AbortSignal.timeout(wait),
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const type = response.headers.get("content-type") ?? "";
  return { status: response.status, type, bytes, value: type.startsWith(HYDRA) && bytes.length ? objectIdsAsHex(new HydraDecoder(bytes).readValue(), base) : bytes.toString() };
}

// Paths (a.b[3].c) where two decoded values differ.
function differences(a, b, path = "", out = []) {
  if (a === b || (Number.isNaN(a) && Number.isNaN(b))) return out;
  if (a && b && typeof a === "object" && typeof b === "object" && Array.isArray(a) === Array.isArray(b) && !Buffer.isBuffer(a)) {
    const keysA = Object.keys(a), keysB = Object.keys(b);
    if (!Array.isArray(a) && keysA.join("\u0000") !== keysB.join("\u0000") && keysA.length === keysB.length && keysA.every(k => k in b)) out.push(`${path} (key order)`);
    for (const k of new Set([...keysA, ...keysB])) differences(a[k], b[k], Array.isArray(a) ? `${path}[${k}]` : `${path}.${k}`, out);
    return out;
  }
  if (Buffer.isBuffer(a) && Buffer.isBuffer(b) && a.equals(b)) return out;
  out.push(path || "(root)");
  return out;
}

// Inside its own batches the TS server hands a Mongo document's ObjectId to the Hydra encoder as it is, which writes
// its twelve bytes as a map ({buffer: {0: .., .., 11: ..}}); outside a batch the same handler's answer has the hex
// string, and so has C#'s. Both sides get the hex string before comparing; how often each side had the map is
// printed (C#'s count is the TS server's forwarded answers passed on).
const objectIdMaps = new Map();
function objectIdsAsHex(value, side) {
  if (!value || typeof value !== "object") return value;
  const keys = Object.keys(value);
  if (keys.length === 1 && keys[0] === "buffer" && value.buffer && typeof value.buffer === "object" && Object.keys(value.buffer).join() === "0,1,2,3,4,5,6,7,8,9,10,11") {
    objectIdMaps.set(side, (objectIdMaps.get(side) ?? 0) + 1);
    return Buffer.from(Object.values(value.buffer)).toString("hex");
  }
  for (const k of keys) value[k] = objectIdsAsHex(value[k], side);
  return value;
}

// Hydra's compressed values (0x67, index 1, a byte string holding zlib data): C# must pass the TS server's on byte for
// byte, as decoding and encoding one again gives other bytes.
function compressedBlocks(bytes) {
  const blocks = [];
  for (let i = 0; i + 4 < bytes.length; i++) {
    if (bytes[i] !== 0x67 || bytes[i + 1] !== 1) continue;
    const width = { 0x33: 1, 0x34: 2, 0x35: 4 }[bytes[i + 2]];
    if (!width || i + 3 + width > bytes.length) continue;
    const length = width === 1 ? bytes[i + 3] : width === 2 ? bytes.readUInt16BE(i + 3) : bytes.readUInt32BE(i + 3);
    const start = i + 3 + width;
    if (bytes[start] !== 0x78 || start + length > bytes.length) continue; // zlib data starts with 0x78
    blocks.push(bytes.subarray(i, start + length));
    i = start + length - 1;
  }
  return blocks;
}

// One sub-request sent to C# on its own, as the game would send it outside a batch.
async function alone(sub, tok, ip) {
  const headers = { ...(sub.headers ?? {}), "content-type": HYDRA, "x-hydra-access-token": tok, "x-real-ip": ip };
  let body;
  if (sub.body !== undefined) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(sub.body);
    body = encoder.returnValue();
  }
  const response = await fetch(`${csUrl}${sub.url}`, { method: sub.verb, headers, body, signal: AbortSignal.timeout(60000) });
  const bytes = Buffer.from(await response.arrayBuffer());
  return { status_code: response.status, headers: {}, body: bytes.length ? new HydraDecoder(bytes).readValue() : null };
}

// A C# item that must not be compared with the TS server's: the same sub-request answered by C# on its own must give
// the same item (two lone answers set aside what moves). Used where C# corrects the TS answer on purpose (/matches/all
// and the username search; matches_diff.mjs and search_diff.mjs check those corrections) and for routes the TS server
// cannot answer inside a batch at all.
async function sameAsAlone(item, sub, tok, ip) {
  const [a, b] = [await alone(sub, tok, ip), await alone(sub, tok, ip)];
  const moving = new Set(differences(a, b));
  return differences(item, b).filter(p => !moving.has(p));
}
const CORRECTED = /^\/(matches\/all|profiles\/search_queries)\//;
const csOnly = id => [
  { verb: "GET", url: "/file_storage", headers: {} },
  { verb: "GET", url: "/leaderboards/ranked_season5_1v1_all/show?count=5", headers: {} },
  { verb: "GET", url: `/leaderboards/ranked_season5_1v1_all/around/${id}`, headers: {} },
  { verb: "GET", url: `/matches/all/${id}?count=3&page=1`, headers: {} },
];

const general = p => p.replace(/\[\d+\]/g, "[]");
let batches = 0, identicalBytes = 0, problems = 0, hangs = 0, checkedAlone = 0, lengthChecked = 0, blocksChecked = 0;
await setting("Batch:ForwardTimeoutSeconds", String(FORWARD_TIMEOUT));
const volatile = new Map();
const report = [];
for (const mode of ["mixed", "all-ts"]) {
  await setForwardRoutes(mode === "mixed" ? "" : ALL_TS);
  for (const [index, player] of players.entries()) {
    const id = player._id.toHexString();
    const session = await login(player, index);
    if (session.id !== id) {
      problems++;
      report.push(`${mode} ${id}: /access answered ${session.status} for account ${session.id ?? "none"}, not this player; skipped`);
      continue;
    }
    const tok = session.token, ip = session.ip;
    if (mode === "mixed") {
      const requests = csOnly(id);
      const cs = await send(csUrl, { options: { allow_failures: true, parallel: true }, requests }, tok, ip);
      for (const [i, sub] of requests.entries()) {
        const found = await sameAsAlone(cs.value?.responses?.[i], sub, tok, ip);
        checkedAlone++;
        if (found.length) {
          problems++;
          report.push(`${mode} ${id} C#-only ${sub.url}: ${found.slice(0, 6).join(", ")}`);
        }
      }
    }
    const cases = [...[...shapes.values()].map(s => ({ name: s.name, batch: JSON.parse(s.text.replaceAll("{id}", id)) })),
      { name: "synthetic", batch: synthetic(id, player.name ?? "abc") }];
    for (const { name, batch } of cases) {
      batches++;
      const ts1 = await send(tsUrl, batch, tok, ip, TS_WAIT).catch(e => (e.name === "TimeoutError" ? null : Promise.reject(e)));
      const started = Date.now();
      const cs = await send(csUrl, batch, tok, ip);
      if (ts1 === null) {
        hangs++;
        const timedOut = cs.value?.responses?.filter(r => r.status_code === 504).length ?? 0;
        const ok = cs.status === 200 && cs.value?.responses?.length === batch.requests.length && Date.now() - started < (FORWARD_TIMEOUT + 5) * 1000;
        if (!ok) problems++;
        if (report.length < 40) report.push(`${mode} ${id} ${name}: TS never answered; C# ${cs.status} in ${Date.now() - started} ms, ${timedOut} of ${cs.value?.responses?.length} items 504${ok ? "" : " (WRONG)"}`);
        continue;
      }
      const ts2 = await send(tsUrl, batch, tok, ip);
      const moving = new Set(differences(ts1.value, ts2.value));
      for (const p of moving) volatile.set(general(p), (volatile.get(general(p)) ?? 0) + 1);
      const found = [];
      if (cs.status !== ts2.status || cs.type.split(";")[0] !== ts2.type.split(";")[0]) found.push(`status/type ${cs.status} ${cs.type} vs ${ts2.status} ${ts2.type}`);
      const corrected = new Set(mode === "mixed" ? batch.requests.flatMap((r, i) => (CORRECTED.test(r.url) ? [i] : [])) : []);
      found.push(...differences(cs.value, ts2.value).filter(p => !moving.has(p) && !corrected.has(Number(p.match(/^\.responses\[(\d+)\]/)?.[1]))));
      for (const i of corrected) {
        found.push(...(await sameAsAlone(cs.value?.responses?.[i], batch.requests[i], tok, ip)).map(p => `[${i}] vs C# alone: ${p}`));
        checkedAlone++;
      }
      if (moving.size === 0 && corrected.size === 0 && !found.length && !cs.bytes.equals(ts2.bytes)) found.push(`bytes differ (${cs.bytes.length} vs ${ts2.bytes.length})`);
      if (cs.bytes.equals(ts2.bytes)) identicalBytes++;
      // What moves is fixed-width (dates, ids, rand), so equal TS lengths mean C#'s must be the same length too: a
      // number written as another type, or a re-encoded compressed block, changes the length.
      if (corrected.size === 0 && ts1.bytes.length === ts2.bytes.length) {
        lengthChecked++;
        if (cs.bytes.length !== ts2.bytes.length) found.push(`length ${cs.bytes.length} vs ${ts2.bytes.length}`);
      }
      for (const block of compressedBlocks(ts2.bytes)) {
        blocksChecked++;
        if (cs.bytes.indexOf(block) < 0) found.push(`compressed block of ${block.length} bytes not passed on as it came`);
      }
      if (found.length) {
        problems++;
        if (report.length < 40) report.push(`${mode} ${id} ${name}: ${found.slice(0, 8).join(", ")}${found.length > 8 ? ` (+${found.length - 8})` : ""}`);
      }
    }
  }
}
await setForwardRoutes("");
await setting("Batch:ForwardTimeoutSeconds", "30");

console.log(`${hangs} batches the TS server never answered; ${checkedAlone} C# items checked against C# alone; ObjectId byte maps turned into hex: ${[...objectIdMaps].map(([k, v]) => `${k} ${v}`).join(", ") || "none"}`);
console.log(`${batches} batches (${shapes.size} captured shapes + synthetic, ${players.length} players, 2 modes): ${problems} with differences, ${identicalBytes} byte-identical, ${lengthChecked} length-checked, ${blocksChecked} compressed blocks checked`);
console.log(`set aside (changes between two TS answers):${volatile.size ? "" : " nothing"}`);
for (const [p, n] of [...volatile].sort()) console.log(`    ${p} x${n}`);
for (const line of report) console.log("  " + line);
process.exit(problems ? 1 : 0);
