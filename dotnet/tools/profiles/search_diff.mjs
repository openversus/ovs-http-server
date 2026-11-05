// GET /profiles/search_queries/get-by-username/run on the TS server and the C# port. The route only reads
// (playertesters, dataassets, online_players), so both servers can run against the same stores, real data included.
// Run from the repository root:
//
//   REF_MONGO_URI=mongodb://127.0.0.1:27017/<db> REF_JWT_SECRET=<both servers' JWT_SECRET> \
//     node dotnet/tools/profiles/search_diff.mjs replay <tsUrl> <csUrl> [players]
//
// Queries are cut from real names (whole, other case, prefix, middle) plus some edge cases. The C# port changes two
// things on purpose (see ProfilesService): the text is matched literally, and exact matches then names that start with
// it come first before the 25-result cut. So for each query:
//   - every player both answers contain has the same entry (dates set aside), and
//   - when the TS answer is complete (under 25) and the text has no regex characters, both hold the same players;
//   - the C# order is exact, then prefix, then the rest, each by account id; every C# player's name contains the text
//     (JavaScript's case folding); and a complete C# answer (under 25) holds every such player.
import { require } from "../refdiff/refdiff.mjs";

const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const [, , command, tsUrl, csUrl, sample] = process.argv;
if (command !== "replay" || !tsUrl || !csUrl) {
  console.error("usage: search_diff.mjs replay <tsUrl> <csUrl> [players]");
  process.exit(2);
}

const mongo = new MongoClient(process.env.REF_MONGO_URI);
await mongo.connect();
const players = await mongo.db().collection("playertesters").find({}, { projection: { name: 1 } }).toArray();
await mongo.close();
const names = players.map((p) => ({ id: p._id.toHexString(), name: typeof p.name === "string" ? p.name : null }));

// A fixed sample of names (every nth), and the queries cut from each.
const step = Math.max(1, Math.floor(names.length / Number(sample || 400)));
const queries = new Set(["a", "e", "zz", "Unknown", "OpenVersus", " ", "a.b", ".", "(", "[a", "x+", "\\", "*", "?", "^", "$", "|", "♡", "É", "ß"]);
for (let i = 0; i < names.length; i += step) {
  const n = names[i].name;
  if (!n) continue;
  queries.add(n).add(n.toLowerCase()).add(n.toUpperCase()).add(n.slice(0, 3));
  if (n.length > 5) queries.add(n.slice(2, 5));
}

const token = jwt.sign({ id: "0000000000000000000a0999" }, process.env.REF_JWT_SECRET);
const REGEX = /[\\^$.|?*+()[\]{}]/;
const contains = (name, q) => typeof name === "string" && name.toLowerCase().includes(q.toLowerCase());
const rank = (name, q) => (name.toLowerCase() === q.toLowerCase() ? 0 : name.toLowerCase().startsWith(q.toLowerCase()) ? 1 : 2);

async function get(base, q, hydra) {
  const response = await fetch(`${base}/profiles/search_queries/get-by-username/run?username=${encodeURIComponent(q)}`, {
    headers: { "x-hydra-access-token": token, ...(hydra ? { "content-type": "application/x-ag-binary" } : {}) },
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const type = response.headers.get("content-type") ?? "";
  const body = type.startsWith("application/x-ag-binary") ? new HydraDecoder(bytes).readValue() : JSON.parse(bytes.toString("utf8"));
  for (const r of body?.results ?? []) {
    // new Date(): an empty map in Hydra, the time in JSON.
    for (const k of ["updated_at", "created_at"]) if (typeof r.result?.[k] === "string") r.result[k] = "<now>";
  }
  return { status: response.status, body };
}

const problems = [];
let checked = 0, complete = 0, entries = 0;
const cases = [...queries].flatMap((q) => [{ q, hydra: true }, ...(q.length <= 3 ? [{ q, hydra: false }] : [])]);
let next = 0;
async function worker() {
  while (next < cases.length) {
    const { q, hydra } = cases[next++];
    const [ts, cs] = await Promise.all([get(tsUrl, q, hydra), get(csUrl, q, hydra)]);
    const fail = (why) => problems.push({ q, hydra, why });
    checked++;
    if (ts.status !== cs.status) { fail(`status ${ts.status} vs ${cs.status}`); continue; }
    const tsById = new Map((ts.body.results ?? []).map((r) => [r.result.id, r]));
    const csResults = cs.body.results ?? [];
    if (cs.body.count !== csResults.length || cs.body.total !== csResults.length || cs.body.cursor !== null || cs.body.start !== 0) fail("page fields");
    for (const r of csResults) {
      const t = tsById.get(r.result.id);
      if (t) {
        entries++;
        if (JSON.stringify(t) !== JSON.stringify(r)) fail(`entry ${r.result.id} differs: ${JSON.stringify(t).slice(0, 300)} | ${JSON.stringify(r).slice(0, 300)}`);
      }
      const name = names.find((n) => n.id === r.result.id)?.name;
      if (!contains(name, q)) fail(`${r.result.id} (${name}) does not contain the text`);
    }
    // Order: rank, then account id.
    const keys = csResults.map((r) => { const name = names.find((n) => n.id === r.result.id).name; return [rank(name, q), r.result.id]; });
    for (let i = 1; i < keys.length; i++) {
      if (keys[i - 1][0] > keys[i][0] || (keys[i - 1][0] === keys[i][0] && keys[i - 1][1] > keys[i][1])) { fail(`order at ${i}`); break; }
    }
    const everyMatch = names.filter((n) => contains(n.name, q));
    if (csResults.length < 25 && csResults.length !== everyMatch.length) fail(`${csResults.length} results, ${everyMatch.length} names contain the text`);
    if (csResults.length > 25) fail("more than 25");
    if (!REGEX.test(q) && (ts.body.results ?? []).length < 25) {
      complete++;
      const a = [...tsById.keys()].sort().join(), b = csResults.map((r) => r.result.id).sort().join();
      if (a !== b) fail(`players differ from the TS server's: ${tsById.size} vs ${csResults.length}`);
    }
  }
}
await Promise.all(Array.from({ length: 8 }, worker));
console.log(`${checked} queries (${queries.size} texts), ${complete} complete TS answers compared as sets, ${entries} entries compared: ${problems.length} problems`);
for (const p of problems.slice(0, 10)) console.log(JSON.stringify(p));
process.exit(problems.length ? 1 : 0);
