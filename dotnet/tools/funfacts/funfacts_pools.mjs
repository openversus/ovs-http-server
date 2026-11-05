// The TS server's fun facts for real players, for the C# port to be checked against: for every playerstats document
// (and the player's eloratings one), the whole pool of facts getRandomFunFact picks from (src/services/funFactsService.ts),
// in order. Writes JSON lines {account_id, stats, elo, pool} to <out.jsonl>; the C# test FunFactsMatchTheTsServer reads it
// (FUNFACTS_GOLDEN=<out.jsonl>). The documents are players' data: keep the file local.
// Run from the repository root, against any database with playerstats (read only):
//   FUNFACTS_MONGO_URI=mongodb://... node -r @swc-node/register dotnet/tools/funfacts/funfacts_pools.mjs <out.jsonl> [limit]
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const repo = process.cwd();
const [, , outFile, limit] = process.argv;
if (!outFile || !process.env.FUNFACTS_MONGO_URI) throw new Error("usage: FUNFACTS_MONGO_URI=... funfacts_pools.mjs <out.jsonl> [limit]");

// The generator list is not exported: a copy of the module that exports it, its imports pointed back at the source.
const source = fs.readFileSync(path.join(repo, "src/services/funFactsService.ts"), "utf8")
  .replaceAll('from "../', `from "${repo}/src/`)
  + "\nexport { FACT_GENS };\n";
const dir = fs.mkdtempSync(path.join(os.tmpdir(), "funfacts-"));
const copy = path.join(dir, "funFactsService.ts");
fs.writeFileSync(copy, source);
const { FACT_GENS } = require(copy);
fs.rmSync(dir, { recursive: true });

const { MongoClient } = require(repo + "/node_modules/mongodb");
const client = new MongoClient(process.env.FUNFACTS_MONGO_URI);
await client.connect();
const db = client.db();
const out = fs.openSync(outFile, "w");
let n = 0, withFacts = 0;
const cursor = db.collection("playerstats").find({}, { sort: { _id: 1 }, limit: Number(limit || 0) });
for await (const stats of cursor) {
  const elo = await db.collection("eloratings").findOne({ account_id: stats.account_id });
  // As getRandomFunFact builds it, from the lean documents.
  const ctx = {
    stats, elo: elo || {}, agg: stats.aggregate || {}, chars1v1: stats.characters_1v1 || {}, chars2v2: stats.characters_2v2 || {},
    recent1v1: stats.recent_matches_1v1 || [], recent2v2: stats.recent_matches_2v2 || [],
  };
  const pool = [];
  for (const gen of FACT_GENS) {
    try {
      const fact = gen(ctx);
      if (fact) pool.push(fact);
    } catch {
      // as there: a failing generator is skipped
    }
  }
  if (pool.length) withFacts++;
  fs.writeSync(out, JSON.stringify({ account_id: stats.account_id, stats, elo, pool }) + "\n");
  n++;
}
fs.closeSync(out);
await client.close();
console.log(`${n} players (${withFacts} with facts), ${FACT_GENS.length} generators -> ${outFile}`);
process.exit(0);
