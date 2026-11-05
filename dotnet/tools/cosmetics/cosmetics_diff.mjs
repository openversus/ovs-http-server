// GET /ssc/invoke/get_equipped_cosmetics on the TS server and the C# port. Run from the repository root (it uses the TS
// server's node_modules):
//
//   node dotnet/tools/cosmetics/cosmetics_diff.mjs run <baseUrl> <out.json>     # scenarios on scratch stores
//   node dotnet/tools/cosmetics/cosmetics_diff.mjs diff <ts.json> <cs.json>
//   node dotnet/tools/cosmetics/cosmetics_diff.mjs replay <tsUrl> <csUrl> [limit] # real cosmetics documents
//
// The deliberate difference: the C# answer carries the six fields WB sent, in WB's order (Taunts, Banner, RingoutVfx,
// AnnouncerPack, StatTrackers, Gems); the TS answer also carries the stored document's _id, account_id and __v. Every
// TS answer is rebuilt that way (the other fields in that order, nothing else changed) and must then be the C# answer
// byte for byte, Hydra and JSON. The Redis value (player:{id}:cosmetics, which TS services read) is compared as is.
//
// run: REF_REDIS_URL, REF_MONGO_URI (scratch; wiped), REF_JWT_SECRET, REF_PROFILE=1, REF_DATA_ASSET_TOKEN (the TS
// server reads dataassets at startup; POST /syncAsset makes it reload them after seeding, and bumps its config CRC,
// which is why config is not compared).
// replay: REF_MONGO_URI and REF_REDIS_URL of the stores both servers use, REF_JWT_SECRET. For each player with a
// cosmetics document: its Redis key deleted, TS asked (it writes the key), the key read and deleted, C# asked, the key
// read; then both asked again (the cached path). Players need a connection record to be resolved (the TS handler finds
// the player that way): the replay writes connections:{id} {id} for each and deletes it afterwards.
import { require, need, openScratch, readProfile, dump, writeRun, diff, toPlain, reloadAssets } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { MongoClient, ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const { createClient } = require(process.cwd() + "/node_modules/redis");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const PATH = "/ssc/invoke/get_equipped_cosmetics";
const WB = ["Taunts", "Banner", "RingoutVfx", "AnnouncerPack", "StatTrackers", "Gems"];
const oid = (n) => "0000000000000000000d" + n.toString(16).padStart(4, "0");

// The TS answer as the C# port sends it: WB's fields in WB's order.
function wbShape(answer) {
  const ec = answer?.body?.EquippedCosmetics;
  if (!ec || typeof ec !== "object") return answer;
  const shaped = {};
  for (const f of WB) if (f in ec) shaped[f] = ec[f];
  return { ...answer, body: { ...answer.body, EquippedCosmetics: shaped } };
}

async function call(baseUrl, token, hydra) {
  const response = await fetch(baseUrl + PATH, {
    headers: { "x-hydra-access-token": token, "x-real-ip": "198.51.100.9", ...(hydra ? { "content-type": "application/x-ag-binary" } : {}) },
    signal: AbortSignal.timeout(10000),
  }).catch((e) => ({ status: `<no answer: ${e.name}>`, arrayBuffer: async () => new ArrayBuffer(0) }));
  const bytes = Buffer.from(await response.arrayBuffer());
  return { status: response.status, bytes, hydra };
}

// An answer rebuilt in WB's shape and encoded as the server would send it.
function reshaped(answer) {
  if (!answer.bytes.length) return answer;
  const value = answer.hydra ? new HydraDecoder(answer.bytes).readValue() : JSON.parse(answer.bytes.toString());
  const shaped = wbShape(value);
  // An answer already in WB's shape (the C# port's) keeps its own bytes: only the TS answer is rebuilt.
  const keys = (v) => Object.keys(v?.body?.EquippedCosmetics ?? {}).join();
  if (keys(value) === keys(shaped)) return answer;
  if (!answer.hydra) return { ...answer, bytes: Buffer.from(JSON.stringify(shaped)) };
  const encoder = new HydraEncoder();
  encoder.encodeValue(shaped);
  return { ...answer, bytes: encoder.returnValue() };
}

const decoded = (answer) => (answer.bytes.length ? toPlain(answer.hydra ? new HydraDecoder(answer.bytes).readValue() : JSON.parse(answer.bytes.toString())) : null);

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("cosmetics_diff");
  const asset = (assetType, slug, character_slug = "", enabled = true) => ({ assetType, slug, character_slug, enabled, assetPath: `/Game/${assetType}/${slug}` });
  await db.collection("dataassets").insertMany([
    asset("CharacterData", "character_shaggy"), asset("CharacterData", "character_taz"), asset("CharacterData", "character_finn"),
    asset("CharacterData", "character_C022"), // a test character: left out
    asset("CharacterData", "character_off", "", false), // disabled: left out
    asset("TauntData", "taunt_shaggy_1", "character_shaggy"), asset("TauntData", "taunt_shaggy_2", "character_shaggy"),
    asset("TauntData", "taunt_taz_1", "character_taz"), asset("TauntData", "taunt_c022_1", "character_C022"),
  ]);
  await reloadAssets(baseUrl, await db.collection("dataassets").findOne({ slug: "character_shaggy" }));
  const session = (n) => redis.hSet(`connections:${oid(n)}`, { id: oid(n) });
  for (const n of [1, 2, 3, 4, 6]) await session(n);
  await db.collection("cosmetics").insertMany([
    { _id: new ObjectId(oid(2)), account_id: new ObjectId(oid(2)), Taunts: { character_taz: { TauntSlots: ["mine", "", "", ""] }, character_gone: { TauntSlots: ["x"] } }, AnnouncerPack: "announcer_x", Banner: "banner_x", StatTrackers: { StatTrackerSlots: ["a", "b", "c"] }, RingoutVfx: "vfx_x", Gems: { GemSlots: ["g", "", ""] }, __v: 0 },
    { _id: new ObjectId(oid(6)), account_id: new ObjectId(oid(6)), Banner: "banner_y", Taunts: { character_shaggy: null }, __v: 0 },
  ]);
  await redis.set(`player:${oid(3)}:cosmetics`, JSON.stringify({ _id: oid(3), account_id: oid(3), AnnouncerPack: "announcer_c", Gems: { GemSlots: ["", "", ""] }, Banner: "", __v: 0 }));
  await redis.set(`player:${oid(4)}:cosmetics`, "");

  const secret = need("REF_JWT_SECRET");
  const steps = [];
  const step = async (name, n, hydra = false) => {
    const answer = reshaped(await call(baseUrl, jwt.sign({ id: oid(n) }, secret), hydra));
    steps.push({ name, response: { status: answer.status, bytes: answer.bytes.toString("base64"), value: decoded(answer) }, state: await dump(redis, db, ["dataassets", "config"]) });
  };
  await step("no-document-created", 1);
  await step("cached-after-create", 1);
  await step("cached-after-create-hydra", 1, true);
  await step("stored-document", 2);
  await step("stored-document-hydra", 2, true);
  await step("cached-missing-fields", 3);
  await step("cached-empty-string", 4);
  await step("stored-null-taunt", 6);
  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "cosmetics_diff"));
  await close();
}

async function replay(tsUrl, csUrl, limit) {
  const mongo = new MongoClient(need("REF_MONGO_URI"));
  await mongo.connect();
  const ids = (await mongo.db().collection("cosmetics").find({}, { projection: { _id: 1 } }).sort({ _id: 1 }).limit(Number(limit ?? 0)).toArray()).map((d) => d._id.toHexString());
  await mongo.close();
  const redis = createClient({ url: need("REF_REDIS_URL") });
  await redis.connect();
  const secret = need("REF_JWT_SECRET");
  let answers = 0;
  const problems = [];
  for (const id of ids) {
    const token = jwt.sign({ id }, secret);
    const key = `player:${id}:cosmetics`;
    const hadSession = await redis.exists(`connections:${id}`);
    if (!hadSession) await redis.hSet(`connections:${id}`, { id });
    for (const hydra of [false, true]) {
      await redis.del(key);
      const ts = reshaped(await call(tsUrl, token, hydra));
      const tsRedis = await redis.get(key);
      await redis.del(key);
      const cs = await call(csUrl, token, hydra);
      const csRedis = await redis.get(key);
      const [tsCached, csCached] = [reshaped(await call(tsUrl, token, hydra)), await call(csUrl, token, hydra)];
      answers += 4;
      if (ts.status !== cs.status || !ts.bytes.equals(cs.bytes)) problems.push(`${id} ${hydra ? "hydra" : "json"} mongo path: ${ts.status}/${ts.bytes.length} vs ${cs.status}/${cs.bytes.length}`);
      if (tsRedis !== csRedis) problems.push(`${id} redis value: ${tsRedis?.length} vs ${csRedis?.length}`);
      if (tsCached.status !== csCached.status || !tsCached.bytes.equals(csCached.bytes)) problems.push(`${id} ${hydra ? "hydra" : "json"} cached path`);
    }
    if (!hadSession) await redis.del(`connections:${id}`);
  }
  await redis.quit();
  console.log(`${answers} answers (${ids.length} players with cosmetics x2 encodings x2 paths) + ${ids.length * 2} Redis values: ${problems.length} differences`);
  for (const p of problems.slice(0, 20)) console.log("  " + p);
  process.exitCode = problems.length ? 1 : 0;
}

const [, , command, a, b, c] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true });
else if (command === "replay" && a && b) await replay(a, b, c);
else {
  console.error("usage: cosmetics_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json> | replay <tsUrl> <csUrl> [limit]");
  process.exit(2);
}
