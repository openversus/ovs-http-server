// PUT /ssc/invoke/update_player_preferences on the TS server and the C# port. Run from the repository root (it uses the
// TS server's node_modules):
//
//   node dotnet/tools/ssc/preferences_diff.mjs run <baseUrl> <out.json>     # on scratch stores
//   node dotnet/tools/ssc/preferences_diff.mjs diff <ts.json> <cs.json>
//
// run: REF_REDIS_URL, REF_MONGO_URI (scratch; wiped), REF_JWT_SECRET, REF_PROFILE=1. Every step records the answer
// (status, content type, bytes) and both stores after it; diff compares them and the Mongo writes each server sent
// (findAndModify on playertesters included).
import { require, need, openScratch, readProfile, dump, writeRun, diff } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const oid = (n) => "0000000000000000000c" + n.toString(16).padStart(4, "0");
const IP = "198.51.100.40";

async function call(baseUrl, claims, body, hydra) {
  let payload = JSON.stringify(body);
  if (hydra) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
  }
  const response = await fetch(`${baseUrl}/ssc/invoke/update_player_preferences`, {
    method: "PUT",
    headers: { "x-hydra-access-token": jwt.sign(claims, need("REF_JWT_SECRET")), "x-real-ip": IP, "content-type": hydra ? "application/x-ag-binary" : "application/json" },
    body: payload,
    signal: AbortSignal.timeout(15000),
  }).catch((e) => ({ status: `<no answer: ${e.name}>`, headers: new Headers(), arrayBuffer: async () => new ArrayBuffer(0) }));
  const bytes = Buffer.from(await response.arrayBuffer());
  return { status: response.status, type: response.headers.get("content-type"), bytes: bytes.toString("base64") };
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("preferences_diff");
  const player = (n, extra = {}) => ({ _id: new ObjectId(oid(n)), name: `p${n}`, GameplayPreferences: 964, __v: 0, ...extra });
  await db.collection("playertesters").insertMany([player(1), player(2), player(3), player(5), player(6)]);
  const connection = (n) => ({ id: oid(n), username: `p${n}`, hydraUsername: `hp${n}`, character: "character_shaggy", GameplayPreferences: "964", current_ip: IP });
  for (const n of [1, 2, 3, 4, 5]) await redis.hSet(`connections:${oid(n)}`, connection(n));
  // The IP-keyed mirror: owned by player 5, and later by someone else.
  await redis.hSet(`connections:${IP}`, { id: oid(5), username: "p5", stale: "yes" });

  const prefs = (GameplayPreferences) => ({ AutoPartyPreference: false, CrossplayPreference: 1, GameplayPreferences, Loadout: { Character: "character_shaggy", Skin: "skin_shaggy_default" }, Platform: "PC" });
  const steps = [];
  const step = async (name, claims, body, hydra = false) => {
    const response = await call(baseUrl, claims, body, hydra);
    steps.push({ name, response, state: await dump(redis, db) });
  };
  await step("json", { id: oid(1), current_ip: "203.0.113.1" }, prefs(1000));
  await step("hydra", { id: oid(1), current_ip: "203.0.113.1" }, prefs(972), true);
  await step("missing-value", { id: oid(2), current_ip: "203.0.113.1" }, (({ GameplayPreferences, ...rest }) => rest)(prefs(0)));
  await step("value-as-text", { id: oid(2), current_ip: "203.0.113.1" }, prefs("12"));
  await step("zero", { id: oid(3), current_ip: "203.0.113.1" }, prefs(0));
  await step("no-player-record", { id: oid(4), current_ip: "203.0.113.1" }, prefs(1001));
  await step("ip-mirror-owned", { id: oid(5), current_ip: IP }, prefs(1002));
  await step("no-connection", { id: oid(6), current_ip: "203.0.113.1" }, prefs(1003));
  await redis.hSet(`connections:${IP}`, { id: oid(9) });
  await step("ip-mirror-not-owned", { id: oid(5), current_ip: IP }, prefs(1004));
  await step("not-a-number", { id: oid(3), current_ip: "203.0.113.1" }, prefs("abc"));
  await step("bad-id", { id: "not-an-object-id", current_ip: "203.0.113.1" }, prefs(1005));

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "preferences_diff"));
  await close();
}

const [, , command, a, b] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true, findAndModify: ["playertesters"] });
else {
  console.error("usage: preferences_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
