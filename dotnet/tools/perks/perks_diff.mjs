// GET /ssc/invoke/perks_get_all_pages on the TS server and the C# port, answer by answer and byte by byte, for every
// account with saved perk pages (both servers read the same stores; the route only reads). Run from the repository root:
//
//   REF_MONGO_URI=mongodb://127.0.0.1:27017/<db> REF_JWT_SECRET=<both servers' JWT_SECRET> \
//     node dotnet/tools/perks/perks_diff.mjs <tsUrl> <csUrl> [limit]
//
// Every account in perkpages (or the first <limit>), the accounts with more than one document (the TS server takes the
// first in natural order), accounts with none, and a token with no id; Hydra and JSON each. A session id that is no
// ObjectId is not sent: the TS server never answers it (see PerksService).
import { require } from "../refdiff/refdiff.mjs";

const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const [, , tsUrl, csUrl, limit] = process.argv;
if (!tsUrl || !csUrl || !process.env.REF_MONGO_URI || !process.env.REF_JWT_SECRET) {
  console.error("usage: REF_MONGO_URI=... REF_JWT_SECRET=... perks_diff.mjs <tsUrl> <csUrl> [limit]");
  process.exit(2);
}

const mongo = new MongoClient(process.env.REF_MONGO_URI);
await mongo.connect();
const pages = mongo.db().collection("perkpages");
let ids = (await pages.distinct("account_id")).map((id) => id.toHexString()).sort();
const duplicates = (await pages.aggregate([{ $group: { _id: "$account_id", n: { $sum: 1 } } }, { $match: { n: { $gt: 1 } } }]).toArray())
  .map((d) => d._id.toHexString());
await mongo.close();
if (limit) ids = [...new Set([...ids.slice(0, Number(limit)), ...duplicates])];
const cases = [...ids, "0000000000000000000a0001", "0000000000000000000a0002", null];

async function get(base, id, hydra) {
  const token = jwt.sign(id === null ? { username: "no id" } : { id }, process.env.REF_JWT_SECRET);
  const response = await fetch(`${base}/ssc/invoke/perks_get_all_pages`, {
    headers: { "x-hydra-access-token": token, ...(hydra ? { "content-type": "application/x-ag-binary" } : {}) },
    signal: AbortSignal.timeout(15000),
  });
  return { status: response.status, type: (response.headers.get("content-type") ?? "").split(";")[0], bytes: Buffer.from(await response.arrayBuffer()) };
}

let compared = 0, withPages = 0;
const problems = [];
for (const id of cases) {
  for (const hydra of [true, false]) {
    const [ts, cs] = [await get(tsUrl, id, hydra), await get(csUrl, id, hydra)];
    compared++;
    if (!hydra && ts.bytes.toString().includes("DisplayName")) withPages++;
    if (ts.status !== cs.status || ts.type !== cs.type || !ts.bytes.equals(cs.bytes)) {
      problems.push(`${id ?? "<no id>"} ${hydra ? "hydra" : "json"}: ${ts.status} ${ts.type} ${ts.bytes.length} vs ${cs.status} ${cs.type} ${cs.bytes.length}`);
    }
  }
}
console.log(`${compared} answers (${cases.length} accounts incl. ${duplicates.length} with two documents, x2 encodings; ${withPages} with pages): ${problems.length} differences`);
for (const p of problems.slice(0, 20)) console.log("  " + p);
process.exit(problems.length ? 1 : 0);
