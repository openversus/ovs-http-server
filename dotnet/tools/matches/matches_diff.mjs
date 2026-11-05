// GET /matches/all/{id} on the TS server and the C# port, compared answer by answer. The route only reads
// (playerstats), so both servers can run against the same stores, real data included. Run from the repository root:
//
//   REF_MONGO_URI=mongodb://127.0.0.1:27017/<db> REF_JWT_SECRET=<both servers' JWT_SECRET> \
//     node dotnet/tools/matches/matches_diff.mjs replay <tsUrl> <csUrl> [limit]
//
// Every account in playerstats (or the first <limit>) with the game's query, plus accounts with no stats; a sample of
// them with other count/page values; Hydra and JSON. The C# port corrects some fields (see MatchHistoryService); this
// applies the same corrections to the TS answer, from the stored entries and names, before comparing. Each match's rand and template id are new on every request and are
// set aside before comparing. The Hydra answers must also be the same length: a whole number sent as a double decodes
// the same as an integer. (The C# encoder picks by value, as mvs-dump does, so this is a guard; no mutation of the
// route's own code can reach it.)
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
if (command !== "replay" || !tsUrl || !csUrl) {
  console.error("usage: matches_diff.mjs replay <tsUrl> <csUrl> [limit]");
  process.exit(2);
}

const GAME_QUERY = "count=10&page=1&fields=server_data&templates=2v2_container&templates=1v1_container&templates=ffa_container";
const VARIANTS = ["", "count=20&page=1", "count=3&page=2", "count=3&page=4", "count=0&page=0", "count=-3&page=1", "count=5&page=-1",
  "count=abc&page=x", "count=0x4&page=2", "count=5&count=2&page=1", "count=2.9&page=1.5", "count=1e1", "page=99999999999", "count=99999999999"];

const mongo = new MongoClient(process.env.REF_MONGO_URI);
await mongo.connect();
const statsByAccount = new Map();
for await (const s of mongo.db().collection("playerstats").find({})) statsByAccount.set(s.account_id, s);
const names = new Map();
for await (const p of mongo.db().collection("playertesters").find({}, { projection: { name: 1 } })) {
  if (typeof p.name === "string") names.set(p._id.toHexString(), p.name);
}
await mongo.close();
let ids = [...statsByAccount.keys()].sort();
if (limit) ids = ids.slice(0, Number(limit));
const token = jwt.sign({ id: "0000000000000000000a0999" }, process.env.REF_JWT_SECRET);
const missing = ["0000000000000000000a0001", "not-an-object-id"];

const cases = [];
for (const id of [...ids, ...missing]) cases.push({ id, query: GAME_QUERY, hydra: true });
for (const id of [...ids.slice(0, 150), ...missing]) {
  for (const query of VARIANTS) cases.push({ id, query, hydra: true });
  cases.push({ id, query: GAME_QUERY, hydra: false });
}

async function get(base, { id, query, hydra }) {
  const response = await fetch(`${base}/matches/all/${encodeURIComponent(id)}?${query}`, {
    headers: { "x-hydra-access-token": token, ...(hydra ? { "content-type": "application/x-ag-binary" } : {}) },
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const type = response.headers.get("content-type") ?? "";
  const body = type.startsWith("application/x-ag-binary") ? new HydraDecoder(bytes).readValue() : JSON.parse(bytes.toString("utf8"));
  let length = bytes.length;
  if (base === tsUrl && correct(id, body) && type.startsWith("application/x-ag-binary")) {
    // The corrected answer as the TS server's encoder would have written it.
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    length = encoder.returnValue().length;
  }
  for (const match of body?.matches ?? []) {
    match.rand = typeof match.rand === "number" && match.rand >= 0 && match.rand < 1 ? "<rand>" : match.rand;
    if (/^[0-9a-f]{24}$/.test(match.template?.id)) match.template.id = "<new id>";
  }
  return { status: response.status, hydra: type.startsWith("application/x-ag-binary"), length, body: JSON.stringify(body) };
}

// The corrections, written from the stored data: the winners, their team, the players' names, the mode.
function correct(accountId, body) {
  if (!body?.matches?.length) return false;
  const stats = statsByAccount.get(accountId);
  const entries = new Map([...(stats?.recent_matches_1v1 ?? []), ...(stats?.recent_matches_2v2 ?? [])].map((e) => [e.matchId, e]));
  for (const m of body?.matches ?? []) {
    const e = entries.get(m.id);
    if (!e) throw new Error(`no stored entry for match ${m.id} of ${accountId}`);
    const winners = e.players.filter((p) => p.isWinner === true);
    if (winners.length) {
      m.win = winners.map((p) => p.accountId);
      m.loss = e.players.filter((p) => p.isWinner !== true).map((p) => p.accountId);
      m.winning_team = [winners[0].teamIndex];
    }
    for (const p of m.players.all) {
      if (winners.length) p.data.EndOfMatchStats.WinningTeamIndex = winners[0].teamIndex;
      p.identity.username = names.get(p.account_id) ?? "";
      p.identity.usernames[0].username = names.get(p.account_id) ?? "";
    }
    const is1v1 = e.mode.includes("1v1");
    m.server_data.GameplayConfig.ModeString = e.mode;
    m.template.data.mode = e.mode;
    m.template.max_players = is1v1 ? 2 : 4;
    if (e.mode.toUpperCase() === "FFA") m.criteria.slug = m.name = m.template.name = m.template.slug = "ffa_container";
  }
  return true;
}

let same = 0, matches = 0;
const differences = [];
let next = 0;
async function worker() {
  while (next < cases.length) {
    const c = cases[next++];
    const [ts, cs] = await Promise.all([get(tsUrl, c), get(csUrl, c)]);
    if (ts.status === cs.status && ts.hydra === cs.hydra && ts.body === cs.body && (!ts.hydra || ts.length === cs.length)) {
      same++;
      matches += JSON.parse(ts.body)?.matches?.length ?? 0;
    } else {
      let at = 0;
      while (at < ts.body.length && ts.body[at] === cs.body[at]) at++;
      const around = (b) => b.slice(Math.max(0, at - 150), at + 150);
      differences.push({ ...c, at, ts: { ...ts, body: around(ts.body) }, cs: { ...cs, body: around(cs.body) } });
    }
  }
}
await Promise.all(Array.from({ length: 12 }, worker));
console.log(`${cases.length} requests (${ids.length} accounts): ${same} identical (${matches} matches), ${differences.length} different`);
for (const d of differences.slice(0, 5)) console.log(JSON.stringify(d, null, 1));
process.exit(differences.length ? 1 : 0);
