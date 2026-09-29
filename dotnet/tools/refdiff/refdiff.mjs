// Shared parts of the TS-reference diff harnesses (tools/access/access_diff.mjs, tools/friends/friends_diff.mjs): the
// scratch stores, the state dump, the Mongo profile, and the normalizing diff. Run the harnesses from the repository
// root: they load the TS server's node_modules.
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
    // Canonical EJSON keeps the BSON types ($numberInt vs $numberDouble, $date, $oid).
    mongo[name] = JSON.parse(EJSON.stringify(await db.collection(name).find().sort({ _id: 1 }).toArray(), { relaxed: false }));
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
 */
export function diff(fileA, fileB, { writes = false } = {}) {
  const a = normalize(JSON.parse(fs.readFileSync(fileA, "utf8")), writes);
  const b = normalize(JSON.parse(fs.readFileSync(fileB, "utf8")), writes);
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
function normalize(run, writes) {
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
      return Object.fromEntries(Object.entries(v).map(([k, x]) => [typeof str(k) === "string" ? str(k) : k, value(x)]));
    }
    return v;
  };
  const steps = run.steps.map((step) => ({ name: step.name, response: value(step.response), state: value(step.state) }));
  // Per write: the collection, and for an update its filter and update. An insert's documents are left to the state
  // (stored as sent): the C# driver sends them in a message section the profiler does not record.
  const sent = writes && run.profile
    ? value(run.profile.filter((op) => op.op === "insert" || op.op === "update").map((op) =>
        op.op === "insert" ? { ns: op.ns, op: "insert" } : { ns: op.ns, q: op.command?.q, u: op.command?.u }))
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
