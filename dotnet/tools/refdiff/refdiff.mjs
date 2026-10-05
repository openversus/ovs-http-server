// Shared parts of the TS-reference diff harnesses (tools/access/access_diff.mjs, tools/friends/friends_diff.mjs): the
// scratch stores, the state dump, the Mongo profile, and the normalizing diff. Run the harnesses from the repository
// root: they load the TS server's node_modules.
import net from "node:net";
import zlib from "node:zlib";
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { createClient } = require(process.cwd() + "/node_modules/redis");
const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");
const { EJSON } = require(process.cwd() + "/node_modules/bson");

export { require };

// Set in a Redis this harness has flushed; any other non-empty Redis is refused (the bench's is real dev data).
const SCRATCH_MARKER = "refdiff:scratch";

export function need(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set`);
  return value;
}

/** Connects to REF_REDIS_URL and REF_MONGO_URI and wipes both; refuses stores that do not look like scratch ones. */
export async function openScratch(appName) {
  const redis = createClient({ url: need("REF_REDIS_URL") });
  await redis.connect();
  if ((await redis.dbSize()) > 0 && !(await redis.exists(SCRATCH_MARKER))) {
    await redis.quit();
    throw new Error(`refusing to flush ${process.env.REF_REDIS_URL.replace(/\/\/[^@]*@/, "//***@")}: not empty and not marked as scratch`);
  }
  const mongoClient = new MongoClient(need("REF_MONGO_URI"), { appName });
  await mongoClient.connect();
  const db = mongoClient.db();
  if (!/ref|test|scratch/i.test(db.databaseName)) throw new Error(`refusing to drop ${db.databaseName}: name it *ref*/*test*/*scratch*`);
  await redis.flushDb();
  await redis.set(SCRATCH_MARKER, "1");
  await db.dropDatabase();
  // Every command the server sends to this database, for reading (REF_PROFILE=1); not compared by diff.
  if (process.env.REF_PROFILE) await db.command({ profile: 2 });
  return {
    redis,
    db,
    async close() {
      await redis.quit();
      await mongoClient.close();
    },
  };
}

/** The recorded Mongo commands (REF_PROFILE=1), without the harness's own. */
export async function readProfile(db, appName) {
  if (!process.env.REF_PROFILE) return undefined;
  return JSON.parse(EJSON.stringify(await db.collection("system.profile").find({ ns: { $not: /system\.profile$/ } }).sort({ ts: 1 }).toArray(), { relaxed: true }))
    .filter((op) => !op.command?.listCollections && op.appName !== appName)
    .map((op) => ({ op: op.op, ns: op.ns, command: op.command }));
}

// NaN (the TS server's undefined) and bigints survive JSON as markers.
export function toPlain(value) {
  if (typeof value === "number" && Number.isNaN(value)) return "<NaN>";
  if (typeof value === "bigint") return `<bigint:${value}>`;
  if (Array.isArray(value)) return value.map(toPlain);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, toPlain(v)]));
  return value;
}

/** Every Redis key (type, TTL, value) and every Mongo document (canonical EJSON), sorted; `skip`: collections left out. */
export async function dump(redis, db, skip = []) {
  const keys = {};
  for await (const key of redis.scanIterator({ COUNT: 1000 })) {
    if (key === SCRATCH_MARKER) continue;
    const type = await redis.type(key);
    const ttl = await redis.ttl(key);
    let value;
    if (type === "string") value = await redis.get(key);
    else if (type === "hash") value = Object.fromEntries(Object.entries(await redis.hGetAll(key)).sort());
    else if (type === "set") value = (await redis.sMembers(key)).sort();
    else if (type === "zset") value = await redis.zRangeWithScores(key, 0, -1);
    else if (type === "list") value = await redis.lRange(key, 0, -1);
    keys[key] = { type, ttl, value };
  }
  const mongo = {};
  for (const { name } of await db.listCollections().toArray()) {
    if (name.startsWith("system.") || skip.includes(name)) continue;
    // Canonical EJSON of the raw BSON values keeps the stored types ($numberInt / $numberLong / $numberDouble, $date,
    // $oid). promoteValues: false matters: a promoted value is a JS number, and EJSON then writes any whole number as
    // $numberInt or $numberLong, whatever type was stored (a double 1790000000000 read as a long, 5.0 as an int).
    mongo[name] = JSON.parse(EJSON.stringify(await db.collection(name).find({}, { promoteValues: false }).sort({ _id: 1 }).toArray(), { relaxed: false }));
  }
  return { redis: Object.fromEntries(Object.entries(keys).sort()), mongo: Object.fromEntries(Object.entries(mongo).sort()) };
}

export function writeRun(outFile, baseUrl, ranAt, steps, profile) {
  fs.writeFileSync(outFile, JSON.stringify({ baseUrl, ranAt, steps, profile }, null, 1));
  console.log(`${steps.length} steps -> ${outFile}`);
}

/**
 * Prints what differs between two runs after normalizing; exit code 1 when anything does. With `writes`, the insert
 * and update commands each server sent (both runs recorded with REF_PROFILE=1) are compared too: the stored state
 * cannot show everything (MongoDB 5+ applies an update's new fields in name order, whatever order they were sent in).
 * With `findAndModify` (collection names), those commands on them too (query, update, upsert; the TS server's
 * findOneAndUpdate calls).
 */
export function diff(fileA, fileB, { writes = false, findAndModify = [] } = {}) {
  const a = normalize(JSON.parse(fs.readFileSync(fileA, "utf8")), writes, findAndModify);
  const b = normalize(JSON.parse(fs.readFileSync(fileB, "utf8")), writes, findAndModify);
  let differences = 0;
  const report = (path, x, y) => {
    differences++;
    console.log(`${path}\n  ${fileA}: ${JSON.stringify(x)}\n  ${fileB}: ${JSON.stringify(y)}`);
  };
  const walk = (path, x, y) => {
    if (path.endsWith(".ttl") && typeof x === "number" && typeof y === "number") {
      if (Math.abs(x - y) > 15) report(path, x, y);
      return;
    }
    if (x && y && typeof x === "object" && typeof y === "object" && Array.isArray(x) === Array.isArray(y)) {
      const keysX = Object.keys(x), keysY = Object.keys(y);
      for (const key of new Set([...keysX, ...keysY])) walk(`${path}.${key}`, x[key], y[key]);
      if (!Array.isArray(x) && keysX.join() !== keysY.join() && keysX.length === keysY.length && keysX.every((k) => k in y)) {
        report(`${path} (key order)`, keysX, keysY);
      }
      return;
    }
    if (JSON.stringify(x) !== JSON.stringify(y)) report(path, x, y);
  };
  const steps = Math.max(a.steps.length, b.steps.length);
  for (let i = 0; i < steps; i++) walk(`[${i}:${a.steps[i]?.name ?? b.steps[i]?.name}]`, a.steps[i], b.steps[i]);
  if (writes) {
    if (!a.writes || !b.writes) report("[writes]", !!a.writes, !!b.writes);
    else walk("[writes]", a.writes, b.writes);
  }
  console.log(differences === 0 ? "no differences" : `${differences} difference(s)`);
  process.exitCode = differences === 0 ? 0 : 1;
}

// Values the server makes up (ids, names, tokens, the time) become placeholders numbered in order of first
// appearance, so two runs compare by what was done rather than by the values chosen.
function normalize(run, writes, findAndModify) {
  const maps = new Map();
  const placeholder = (kind, value) => {
    if (!maps.has(kind)) maps.set(kind, new Map());
    const map = maps.get(kind);
    if (!map.has(value)) map.set(value, `<${kind}${map.size + 1}>`);
    return map.get(value);
  };
  const seeded = /^0{12,}[0-9a-f]{4,}$/;
  const near = (n, unit) => Math.abs(n - (unit === "ms" ? run.ranAt : run.ranAt / 1000)) < (unit === "ms" ? 600_000 : 600);
  const str = (s) => {
    if (/^eyJ[\w-]+\.eyJ[\w-]+\.[\w-]+$/.test(s)) {
      const claims = JSON.parse(Buffer.from(s.split(".")[1], "base64url"));
      // exp compares as a lifetime: the two runs are minutes apart.
      if (typeof claims.exp === "number" && typeof claims.iat === "number") claims.exp = `<iat+${claims.exp - claims.iat}>`;
      return { "<jwt>": value(claims) };
    }
    // An ISO time near the run (Date.toISOString: always milliseconds and Z) is "now"; its format still compares.
    if (/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(s) && near(Date.parse(s), "ms")) return "<now-iso>";
    return s
      .replace(/\b[0-9a-f]{24}\b/g, (m) => (seeded.test(m) ? m : placeholder("oid", m)))
      .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b/g, (m) => (m.startsWith("00000000") ? m : placeholder("uuid", m)))
      .replace(/\bOpenVersus_\d{13}\b/g, (m) => placeholder("name", m))
      .replace(/^\d{13}$/, (m) => (near(Number(m), "ms") ? "<now-ms>" : m))
      .replace(/^\d{10}$/, (m) => (near(Number(m), "s") ? "<now-s>" : m));
  };
  const value = (v) => {
    if (typeof v === "string") return str(v);
    if (typeof v === "number") return near(v, "ms") ? "<now-ms>" : near(v, "s") ? "<now-s>" : v;
    if (Array.isArray(v)) return v.map(value);
    if (v && typeof v === "object") {
      if ("$date" in v) {
        const ms = Number(v.$date?.$numberLong ?? Date.parse(v.$date));
        return { $date: near(ms, "ms") ? "<now>" : new Date(ms).toISOString() };
      }
      // A stored number near the run (a timestamp): the time is set aside, its BSON type still compares.
      const [tag] = Object.keys(v);
      if (Object.keys(v).length === 1 && ["$numberInt", "$numberLong", "$numberDouble"].includes(tag) && typeof v[tag] === "string") {
        const n = Number(v[tag]);
        return { [tag]: near(n, "ms") ? "<now-ms>" : near(n, "s") ? "<now-s>" : v[tag] };
      }
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [typeof str(k) === "string" ? str(k) : k, value(x)]));
    }
    return v;
  };
  const steps = run.steps.map((step) => ({ name: step.name, response: value(step.response), state: value(step.state) }));
  // Per write: the collection, and for an update its filter and update. An insert's documents are left to the state
  // (stored as sent): the C# driver sends them in a message section the profiler does not record.
  const sent = writes && run.profile
    ? value(run.profile.filter((op) => op.op === "insert" || op.op === "update" || (findAndModify && op.command?.findAndModify && findAndModify.some((c) => op.ns.endsWith("." + c)))).map((op) =>
        op.op === "insert" ? { ns: op.ns, op: "insert" }
          : op.command?.findAndModify ? { ns: op.ns, findAndModify: op.command.query, update: op.command.update, upsert: op.command.upsert ?? false }
          : { ns: op.ns, q: op.command?.q, u: op.command?.u }))
    : undefined;
  return { ...run, steps, writes: sent };
}

/** POST /syncAsset with an asset as stored: the TS server rewrites it unchanged and reloads its asset cache (it reads
 * dataassets only at startup otherwise). Needs REF_DATA_ASSET_TOKEN, the servers' DATA_ASSET_TOKEN. */
export async function reloadAssets(baseUrl, asset) {
  await fetch(baseUrl + "/syncAsset", {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${need("REF_DATA_ASSET_TOKEN")}` },
    body: JSON.stringify(asset),
  });
  await new Promise((r) => setTimeout(r, 300));
}

// A raw MONITOR connection (the Redis client in node_modules has no monitor mode): each reply line after +OK is one
// command some client sent, as `<time> [<db> <addr>] "CMD" "arg" ...`.
export async function openMonitor(url, onLine) {
  const { hostname, port, username, password } = new URL(url);
  const socket = net.connect(Number(port || 6379), hostname);
  await new Promise((resolve, reject) => { socket.once("connect", resolve); socket.once("error", reject); });
  // +OK for AUTH (when there is a password), then +OK for MONITOR: recording starts after the last.
  let buffer = "", oks = 0, ready;
  const expected = password ? 2 : 1;
  const started = new Promise((resolve) => { ready = resolve; });
  socket.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let at;
    while ((at = buffer.indexOf("\r\n")) >= 0) {
      const line = buffer.slice(0, at);
      buffer = buffer.slice(at + 2);
      if (line === "+OK" && oks < expected) { if (++oks === expected) ready(); }
      else if (line.startsWith("+")) onLine(line.slice(1));
      else if (line.startsWith("-")) throw new Error(`MONITOR: ${line}`);
    }
  });
  const auth = password ? `AUTH ${decodeURIComponent(username || "default")} ${decodeURIComponent(password)}\r\n` : "";
  socket.write(`${auth}MONITOR\r\n`);
  await started;
  return socket;
}

// The writes and publishes in the MONITOR lines, in order, from any client but the harness; reads are left out (the
// two servers read differently: HGETALL vs HMGET).
const WRITES = new Set(["set", "setex", "psetex", "incr", "expire", "pexpire", "publish", "del", "unlink", "hset", "hmset", "hdel", "sadd", "srem", "zadd", "lpush", "rpush"]);
export function writes(lines, self) {
  return lines
    .filter((line) => line.match(/\[\d+ ([^\]]+)\]/)?.[1] !== self)
    .map((line) => [...line.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]))
    .filter((parts) => parts.length && WRITES.has(parts[0].toLowerCase()) && parts[1] !== "refdiff:scratch")
    // The C# Redis client spells some writes differently: SETEX k s v and PSETEX k ms v are SET k v EX s, SET k v PX ms
    // is SET k v EX s, PEXPIRE k ms is EXPIRE k s, HMSET is HSET, UNLINK is DEL. GETDEL is left out with the reads:
    // whether it deleted anything shows in the state after.
    .map((parts) => {
      const [cmd, ...args] = [parts[0].toLowerCase(), ...parts.slice(1)];
      const seconds = (ms) => String(Math.round(Number(ms) / 1000));
      if (cmd === "setex") return ["set", args[0], args[2], "EX", args[1]];
      if (cmd === "psetex") return ["set", args[0], args[2], "EX", seconds(args[1])];
      if (cmd === "set" && args[2]?.toUpperCase() === "PX") return ["set", args[0], args[1], "EX", seconds(args[3]), ...args.slice(4)];
      if (cmd === "pexpire") return ["expire", args[0], seconds(args[1])];
      if (cmd === "hmset") return ["hset", ...args];
      if (cmd === "unlink") return ["del", ...args];
      return [cmd, ...args];
    }).map((parts) => parts.join(" "));
}

export async function state(redis) {
  const out = {};
  for await (const key of redis.scanIterator({ COUNT: 1000 })) {
    if (key === "refdiff:scratch") continue;
    const type = await redis.type(key);
    const ttl = await redis.ttl(key);
    const value = type === "string" ? await redis.get(key) : type === "hash" ? Object.fromEntries(Object.entries(await redis.hGetAll(key)).sort()) : type;
    // A TTL is compared to the minute: both runs set it moments before reading it.
    out[key] = { type, ttl: ttl > 0 ? `~${Math.round(ttl / 60)}m` : ttl, value };
  }
  return Object.fromEntries(Object.entries(out).sort());
}

/**
 * The keys of every Hydra map in a message, in wire order, by path ("server_data.AllMultiplayParams": ["1", "2"]). A
 * decoded answer is a JS object, which puts integer-like keys first whatever order they came in, so only the bytes show
 * the order. Reads what the servers send (no compressed or special values: those are refused).
 */
export function hydraKeyOrders(bytes) {
  let at = 0;
  const orders = {};
  const u = (n) => { let v = 0; for (let i = 0; i < n; i++) v = v * 256 + bytes[at++]; return v; };
  const count = (code, base) => u(1 << (code - base));
  const value = (path) => {
    const code = bytes[at++];
    if (code <= 0x03) return code === 0x02 ? true : code === 0x03 ? false : code === 0x01 ? null : 0;
    if (code >= 0x10 && code <= 0x17) { at += [1, 1, 2, 2, 4, 4, 8, 8][code - 0x10]; return 0; }
    if (code === 0x20) { at += 4; return 0; }
    if (code === 0x21) { at += 8; return 0; }
    if (code >= 0x30 && code <= 0x35) { const n = u([1, 2, 4, 1, 2, 4][code - 0x30]); const s = bytes.subarray(at, at + n).toString("utf8"); at += n; return s; }
    if (code === 0x40) { at += 4; return 0; }
    if (code >= 0x50 && code <= 0x53) { const n = count(code, 0x50); for (let i = 0; i < n; i++) value(`${path}[]`); return 0; }
    if (code >= 0x60 && code <= 0x63) {
      const n = count(code, 0x60), keys = [];
      for (let i = 0; i < n; i++) { const k = String(value(`${path}.<key>`)); keys.push(k); value(`${path}.${k}`); }
      (orders[path || "."] ??= []).push(keys);
      return 0;
    }
    throw new Error(`hydraKeyOrders: code 0x${code.toString(16)} at ${at - 1}`);
  };
  value("");
  return orders;
}

/**
 * A Hydra message split at its compressed values (0x67, index 1, a byte string holding zlib or zstd data): each one's
 * bytes as sent (`blocks`), what each unpacks to (`sections`), which of them are zstd (`zstd`, a count), and the bytes
 * around them (`rest`), where a compressed value is left as its 0x67 0x01 alone, so a length written in another width
 * does not count. Compressors differ in the bytes they make from the same data; `sections` and `rest` are what a
 * compressor cannot change.
 */
export function hydraSections(bytes) {
  const blocks = [], sections = [], rest = [];
  let zstd = 0;
  let from = 0;
  for (let i = 0; i + 4 < bytes.length; i++) {
    if (bytes[i] !== 0x67 || bytes[i + 1] !== 0x01) continue;
    const width = { 0x33: 1, 0x34: 2, 0x35: 4 }[bytes[i + 2]];
    if (!width || i + 3 + width > bytes.length) continue;
    const length = bytes.readUIntBE(i + 3, width);
    const start = i + 3 + width;
    if (start + length > bytes.length) continue;
    // zlib data starts with 0x78, a zstd frame with 28 B5 2F FD.
    const isZstd = bytes.readUInt32BE(start) === 0x28b52ffd;
    if (bytes[start] !== 0x78 && !isZstd) continue;
    let inflated;
    try {
      const data = bytes.subarray(start, start + length);
      inflated = isZstd ? zlib.zstdDecompressSync(data) : zlib.inflateSync(data);
    } catch {
      continue;
    }
    if (isZstd) zstd++;
    rest.push(bytes.subarray(from, i + 2));
    blocks.push(bytes.subarray(i, start + length));
    sections.push(inflated);
    from = start + length;
    i = from - 1;
  }
  rest.push(bytes.subarray(from));
  return { blocks, sections, zstd, rest: Buffer.concat(rest) };
}

/**
 * The same Hydra message with every zstd compressed value rewritten as zlib holding the same bytes, for decoders that
 * read only zlib (mvs-dump). Only for reading values: the bytes are not what either server sent.
 */
export function zstdAsZlib(bytes) {
  const out = [];
  let from = 0;
  for (let i = 0; i + 7 < bytes.length; i++) {
    if (bytes[i] !== 0x67 || bytes[i + 1] !== 0x01) continue;
    const width = { 0x33: 1, 0x34: 2, 0x35: 4 }[bytes[i + 2]];
    if (!width) continue;
    const length = bytes.readUIntBE(i + 3, width);
    const start = i + 3 + width;
    if (start + length > bytes.length || bytes.readUInt32BE(start) !== 0x28b52ffd) continue;
    let deflated;
    try {
      deflated = zlib.deflateSync(zlib.zstdDecompressSync(bytes.subarray(start, start + length)));
    } catch {
      continue;
    }
    const [code, size] = deflated.length <= 0xff ? [0x33, 1] : deflated.length <= 0xffff ? [0x34, 2] : [0x35, 4];
    const header = Buffer.alloc(3 + size);
    header[0] = 0x67;
    header[1] = 0x01;
    header[2] = code;
    header.writeUIntBE(deflated.length, 3, size);
    out.push(bytes.subarray(from, i), header, deflated);
    from = start + length;
    i = from - 1;
  }
  out.push(bytes.subarray(from));
  return Buffer.concat(out);
}
