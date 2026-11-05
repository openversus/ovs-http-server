// PUT /ssc/invoke/perks_set_character_page and PUT /ssc/invoke/perks_absent on the TS server and the C# port. Run from
// the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/perks/perks_write_diff.mjs run <baseUrl> <out.json>     # on scratch stores
//   node dotnet/tools/perks/perks_write_diff.mjs diff <ts.json> <cs.json>
//
// run: REF_REDIS_URL, REF_MONGO_URI (scratch; wiped), REF_JWT_SECRET, REF_PROFILE=1. Every step records the answer
// (status, content type, bytes) and both stores after it; diff compares them and the Mongo writes each server sent
// (findAndModify on perkpages included).
//
// Expected differences, the last two steps (state and their writes; every answer is the same): page-missing-fields,
// where the TS server stores DisplayName, Description and Perks as null and the C# port stores "Custom Set 1", "" and
// []; page-no-character, where the TS server stores the page under "undefined" and the C# port under the player's
// current character (character_wonder_woman here: the player has no connection or record).
import { require, need, openScratch, readProfile, dump, writeRun, diff } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const oid = (n) => "0000000000000000000b" + n.toString(16).padStart(4, "0");

async function call(baseUrl, route, claims, body, hydra) {
  let payload = JSON.stringify(body);
  if (hydra) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
  }
  const response = await fetch(`${baseUrl}/ssc/invoke/${route}`, {
    method: "PUT",
    headers: { "x-hydra-access-token": jwt.sign(claims, need("REF_JWT_SECRET")), "x-real-ip": "198.51.100.9", "content-type": hydra ? "application/x-ag-binary" : "application/json" },
    body: payload,
    signal: AbortSignal.timeout(15000),
  }).catch((e) => ({ status: `<no answer: ${e.name}>`, headers: new Headers(), arrayBuffer: async () => new ArrayBuffer(0) }));
  const bytes = Buffer.from(await response.arrayBuffer());
  return { status: response.status, type: response.headers.get("content-type"), bytes: bytes.toString("base64") };
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("perks_write_diff");
  // A fixed low _id: the dump sorts by _id, and one made here would sort against the server's own at random.
  await db.collection("perkpages").insertOne({
    _id: new ObjectId("000000000000000000000001"),
    account_id: new ObjectId(oid(2)),
    perk_pages: { character_taz: { 0: { DisplayName: "Taz", Description: "mine", Perks: ["a", "b", "c", "d"] } } },
    __v: 0,
  });
  const steps = [];
  const step = async (name, route, claims, body, hydra = false) => {
    const response = await call(baseUrl, route, claims, body, hydra);
    steps.push({ name, response, state: await dump(redis, db) });
  };
  const page = (Character, PageIndex, DisplayName = "Page", Description = "desc", Perks = ["perk_a", "perk_b", "perk_c", "perk_d"]) =>
    ({ Character, PageIndex, DisplayName, Description, Perks });

  await step("page-new-player", "perks_set_character_page", { id: oid(1) }, page("character_shaggy", 0));
  await step("page-second", "perks_set_character_page", { id: oid(1) }, page("character_shaggy", 1, "Two"), true);
  await step("page-overwrite", "perks_set_character_page", { id: oid(1) }, page("character_shaggy", 0, "Again", "", []));
  await step("page-existing-document", "perks_set_character_page", { id: oid(2) }, page("character_shaggy", 0), true);
  await step("page-existing-character", "perks_set_character_page", { id: oid(2) }, page("character_taz", 1));
  await step("page-index-text", "perks_set_character_page", { id: oid(3) }, page("character_finn", "2"));
  await step("page-perks-not-a-list", "perks_set_character_page", { id: oid(3) }, page("character_finn", 3, "x", "y", "perk_a"));
  await step("page-bad-id", "perks_set_character_page", { id: "not-an-object-id" }, page("character_shaggy", 0));

  await step("absent-json", "perks_absent", { id: oid(1) }, { ContainerMatchId: "6abadbdee16b5d000b6c4037" });
  await step("absent-hydra", "perks_absent", { id: oid(1) }, { ContainerMatchId: "6abadbdee16b5d000b6c4037" }, true);

  // Last, the expected differences (a new document would shift every later document in the dump, which is sorted by
  // _id): the TS server stores missing fields as null and a missing character as "undefined"; the C# port fills them
  // in (see PerksService).
  await step("page-missing-fields", "perks_set_character_page", { id: oid(3) }, { Character: "character_finn", PageIndex: 0 });
  await step("page-no-character", "perks_set_character_page", { id: oid(4) }, { PageIndex: 0, DisplayName: "x", Description: "y", Perks: [] });

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "perks_write_diff"));
  await close();
}

const [, , command, a, b] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true, findAndModify: ["perkpages"] });
else {
  console.error("usage: perks_write_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
