// Key-for-key comparison of the login's friends reads between two servers: the TS server (the reference) and the C#
// one. GET /friends/me, /friends/me/invitations/incoming and /outgoing, /social/me/blocked: their answers and every
// write they make (a friendlists document created on first use, blocked ids copied into playertesters.blockedPlayers
// and player:{id}:blocked). Then the profile lookups the friends list makes, /accounts/wb_network/bulk and
// /profiles/bulk (Hydra, sent as the game sends them: PUT with x-hydra-http-method: GET). Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/friends/friends_diff.mjs run <baseUrl> <out.json>   # drives the scenarios, dumps every write
//   node dotnet/tools/friends/friends_diff.mjs diff <ts.json> <cs.json>   # what differs, after normalizing
//   node dotnet/tools/friends/friends_diff.mjs replay <baseUrl> <out.json>       # real data: see replay()
//   node dotnet/tools/friends/friends_diff.mjs replay-diff <ts.json> <cs.json>
//
// Both servers must use the same scratch stores, which `run` wipes first:
//   REF_REDIS_URL   e.g. redis://default:pw@127.0.0.1:16390 (a throwaway Redis: FLUSHDB; a non-empty unmarked one is refused)
//   REF_MONGO_URI   e.g. mongodb://127.0.0.1:27017/ovs_friends_ref (a scratch database: dropped)
//   REF_JWT_SECRET  the JWT secret both servers use (the requests carry session tokens)
//   REF_PROFILE=1   record every Mongo command the server sends: needed, `diff` compares the writes
//   REF_DATA_ASSET_TOKEN  the servers' DATA_ASSET_TOKEN: POST /syncAsset makes the TS server reload its icon cache
//                         after the harness seeds dataassets (it reads them only at startup otherwise)
// Never point these at data you want to keep.
import { require, need, openScratch, readProfile, dump, writeRun, diff, reloadAssets } from "../refdiff/refdiff.mjs";

const { EJSON } = require(process.cwd() + "/node_modules/bson");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;
const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");

const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diff(args[0], args[1], { writes: true });
else if (command === "replay") await replay(args[0], args[1]);
else if (command === "replay-diff") diff(args[0], args[1]);
else {
  console.error("usage: friends_diff.mjs run|replay <baseUrl> <out.json> | diff|replay-diff <a.json> <b.json>");
  process.exit(2);
}

function oid(n) {
  return `0000000000000000000a${String(n).padStart(4, "0")}`;
}

// A playertesters document as the TS model saves a current one; `omit` drops fields to make an older one.
function player(n, fields = {}, omit = []) {
  const doc = {
    _id: new ObjectId(oid(n)), name: `Player ${n}`, hydraUsername: `player-${n}`, ip: `198.51.100.${n % 250}`,
    token: { id: oid(n), username: `Player ${n}` }, account: { id: oid(n), username: `Player ${n}` },
    GameplayPreferences: 964, profile_id: new ObjectId(`0000000000000000000b${String(n).padStart(4, "0")}`),
    public_id: `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`, profile_icon: "profile_icon_default",
    blockedPlayers: [], character: "character_shaggy", variant: "skin_shaggy_default", party_key: "", steamId: "",
    epicId: "", hardwareId: "", hardwareIdVersion: "", hardwareIdQuality: "", installId: "",
    lastSeenAt: new Date("2026-09-01T00:00:00Z"), ipSeenAt: new Date("2026-09-01T00:00:00Z"), provisional: false, __v: 0,
    ...fields,
  };
  for (const field of omit) delete doc[field];
  return doc;
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("friends_diff");
  const secret = need("REF_JWT_SECRET");
  const steps = [];
  const get = async (name, path, id, headers) => {
    const token = id === undefined ? undefined : jwt.sign({ id, username: `Player ${id}` }, secret);
    steps.push({ name, response: await call(baseUrl, path, token, headers), state: await dump(redis, db) });
  };
  const at = (iso) => new Date(iso);
  const players = db.collection("playertesters"), lists = db.collection("friendlists"), requests = db.collection("friendrequests");

  // 1. A player with no friend list yet: /friends/me creates the empty list; the other reads find nothing.
  await players.insertOne(player(101));
  await get("fresh-friends", "/friends/me?page_size=1000&", oid(101));
  await get("fresh-incoming", "/friends/me/invitations/incoming?page_size=1000&state=open&", oid(101));
  await get("fresh-outgoing", "/friends/me/invitations/outgoing?page_size=1000&state=open&", oid(101));
  await get("fresh-blocked", "/social/me/blocked", oid(101));

  // 2. A full list: friends with and without the optional fields (mongoose fills status and addedAt on load, so an
  // entry without status is a friend); blocked players, one already copied to blockedPlayers, two not.
  await players.insertOne(player(102, { blockedPlayers: [oid(901)] }));
  await lists.insertOne({
    _id: new ObjectId("0000000000000000000c0102"), accountId: oid(102), __v: 3,
    friends: [
      { friendAccountId: oid(201), friendUsername: "Friend One", status: "active", addedAt: at("2026-03-24T09:26:19.967Z") },
      { friendAccountId: oid(202), status: "active", addedAt: at("2026-03-25T00:00:00Z") },
      { friendAccountId: oid(203), friendUsername: "No Status", addedAt: at("2026-04-01T12:00:00.5Z") },
      { friendAccountId: oid(204), friendUsername: "No Date", status: "active" },
      { friendAccountId: oid(205), friendUsername: "", status: "active", addedAt: at("2026-05-01T00:00:00Z") },
      { friendAccountId: oid(901), friendUsername: "Blocked Known", status: "blocked", addedAt: at("2026-05-04T03:29:12.942Z") },
      { friendAccountId: oid(902), friendUsername: "Blocked New", status: "blocked", addedAt: at("2026-06-06T00:58:42.770Z") },
      { friendAccountId: oid(903), status: "blocked", addedAt: at("2026-08-01T05:03:44.069Z") },
    ],
  });
  await get("full-friends", "/friends/me?page_size=1000&", oid(102));
  await get("full-friends-again", "/friends/me?page_size=1000&", oid(102));
  await get("full-blocked", "/social/me/blocked", oid(102));

  // 3. Invitations: pending both ways (one without a sender name), and answered ones that must not show.
  await requests.insertMany([
    { _id: new ObjectId("0000000000000000000d0001"), fromAccountId: oid(301), fromUsername: "Asker", toAccountId: oid(102), toUsername: "Player 102", status: "pending", createdAt: at("2026-09-01T00:00:00Z"), updatedAt: at("2026-09-01T00:00:00Z"), __v: 0 },
    { _id: new ObjectId("0000000000000000000d0002"), fromAccountId: oid(302), toAccountId: oid(102), toUsername: "Player 102", status: "pending", __v: 0 },
    { _id: new ObjectId("0000000000000000000d0003"), fromAccountId: oid(303), fromUsername: "Accepted", toAccountId: oid(102), toUsername: "Player 102", status: "accepted", __v: 0 },
    { _id: new ObjectId("0000000000000000000d0004"), fromAccountId: oid(102), fromUsername: "Player 102", toAccountId: oid(304), toUsername: "♡Hatsune~Alice♡", status: "pending", __v: 0 },
    { _id: new ObjectId("0000000000000000000d0005"), fromAccountId: oid(102), fromUsername: "Player 102", toAccountId: oid(305), toUsername: "Declined", status: "declined", __v: 0 },
  ]);
  await get("incoming", "/friends/me/invitations/incoming?page_size=1000&state=open&", oid(102));
  await get("outgoing", "/friends/me/invitations/outgoing?page_size=1000&state=open&", oid(102));

  // 4. An account from before the identity work, with no blockedPlayers at all: the copy also writes the defaults
  // mongoose gives the missing fields. Through /social/me/blocked this time.
  await players.insertOne(player(103, {}, ["blockedPlayers", "installId", "hardwareIdVersion", "hardwareIdQuality", "lastSeenAt", "ipSeenAt", "provisional"]));
  await lists.insertOne({ _id: new ObjectId("0000000000000000000c0103"), accountId: oid(103), friends: [{ friendAccountId: oid(904), friendUsername: "Blocked", status: "blocked", addedAt: at("2026-07-01T00:00:00Z") }], __v: 0 });
  await get("old-account-blocked", "/social/me/blocked", oid(103));

  // 5. A token for an account with no player document: /social/me/blocked writes nothing, /friends/me creates the list.
  await get("no-player-blocked", "/social/me/blocked", oid(104));
  await get("no-player-friends", "/friends/me?page_size=1000&", oid(104));

  // 6. A blockedPlayers entry the list does not have stays (the copy only adds).
  await players.insertOne(player(105, { blockedPlayers: [oid(999)] }));
  await lists.insertOne({ _id: new ObjectId("0000000000000000000c0105"), accountId: oid(105), friends: [{ friendAccountId: oid(206), friendUsername: "Pal", status: "active", addedAt: at("2026-02-01T00:00:00Z") }], __v: 0 });
  await get("extra-blocked-kept", "/friends/me?page_size=1000&", oid(105));

  // 7. No token, and a token from another secret: refused before any of it.
  await get("no-token", "/friends/me?page_size=1000&", undefined);
  steps.push({ name: "bad-token", response: await call(baseUrl, "/social/me/blocked", jwt.sign({ id: oid(102) }, "another-secret-0123456789abcdef0123")), state: await dump(redis, db) });

  // 8. Profile lookups. Icons: the default, one with two entries (the first wins), a disabled one and a test
  // character's (neither counts). The TS server reads icons at startup, so it is told to reload them.
  const now = new Date();
  const icon = (slug, assetPath, fields = {}) => ({ assetType: "ProfileIconData", slug, assetPath, character_slug: "", enabled: true, createdAt: now, updatedAt: now, __v: 0, ...fields });
  await db.collection("dataassets").insertMany([
    icon("profile_icon_default", "/Game/Icons/Default.Default"),
    icon("profile_icon_a", "/Game/Icons/A.A"),
    icon("profile_icon_a", "/Game/Icons/A2.A2"),
    icon("profile_icon_off", "/Game/Icons/Off.Off", { enabled: false }),
    icon("profile_icon_test", "/Game/Icons/Test.Test", { character_slug: "character_Meeseeks" }),
  ]);
  await reloadAssets(baseUrl, { assetType: "ProfileIconData", slug: "profile_icon_default", assetPath: "/Game/Icons/Default.Default", character_slug: "", enabled: true });
  await players.insertMany([
    player(401, { profile_icon: "profile_icon_a" }),
    player(402, { name: "", hydraUsername: "hydra-402", profile_icon: "profile_icon_nope" }),
    player(403, {}, ["name", "hydraUsername", "profile_icon"]),
    player(404, { profile_icon: "profile_icon_off" }),
    player(405, { profile_icon: "profile_icon_test" }),
  ]);
  await redis.sAdd("online_players", [oid(401), oid(404)]);
  const UPDATE = "00000000000000000000a003";
  const ids = [oid(401), oid(402), oid(403), oid(404), oid(405), oid(401).toUpperCase(), "not-an-id", oid(401), UPDATE, oid(999)];
  const bulk = async (name, path, body, json = false) =>
    steps.push({ name, response: await (json ? callJson(baseUrl, path, body) : callHydra(baseUrl, path, body)), state: await dump(redis, db, ["config", "dataassets"]) });
  await bulk("wb-bulk", "/accounts/wb_network/bulk", { ids });
  await bulk("wb-bulk-bare-array", "/accounts/wb_network/bulk", [oid(402)]);
  await bulk("wb-bulk-empty-map", "/accounts/wb_network/bulk", {});
  await bulk("wb-bulk-no-body", "/accounts/wb_network/bulk", undefined);
  await bulk("wb-bulk-ids-string", "/accounts/wb_network/bulk", { ids: "abc" });
  await bulk("profiles-bulk", "/profiles/bulk", { ids: [...ids, UPDATE] });
  await bulk("profiles-bulk-only-virtual", "/profiles/bulk", { ids: [UPDATE] });
  await bulk("profiles-bulk-empty", "/profiles/bulk", { ids: [] });
  await bulk("profiles-bulk-no-ids", "/profiles/bulk", {});
  await bulk("profiles-bulk-ids-string", "/profiles/bulk", { ids: "abc" });
  await bulk("profiles-bulk-json", "/profiles/bulk", { ids: [oid(401), UPDATE] }, true);

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "friends_diff"));
  await close();
}

// As the game sends a bulk lookup: PUT, the real method in x-hydra-http-method, a Hydra body.
async function callHydra(baseUrl, path, value, token = jwt.sign({ id: oid(401) }, need("REF_JWT_SECRET"))) {
  let body;
  if (value !== undefined) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(value);
    body = encoder.returnValue();
  }
  const response = await fetch(baseUrl + path, {
    method: "PUT",
    headers: { "content-type": "application/x-ag-binary", "x-hydra-http-method": "GET", "x-hydra-access-token": token },
    body,
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  let decoded = null;
  if (bytes.length > 0 && (response.headers.get("content-type") || "").includes("x-ag-binary")) {
    decoded = toPlainValue(new HydraDecoder(bytes).readValue());
  }
  return { status: response.status, contentType: response.headers.get("content-type"), length: bytes.length, body: decoded };
}

async function callJson(baseUrl, path, value) {
  const response = await fetch(baseUrl + path, {
    method: "PUT",
    headers: { "content-type": "application/json", "x-hydra-http-method": "GET", "x-hydra-access-token": jwt.sign({ id: oid(401) }, need("REF_JWT_SECRET")) },
    body: JSON.stringify(value),
  });
  const text = await response.text();
  return { status: response.status, contentType: response.headers.get("content-type"), length: Buffer.byteLength(text), body: JSON.parse(text || "null") };
}

function toPlainValue(value) {
  if (typeof value === "bigint") return `<bigint:${value}>`;
  if (Array.isArray(value)) return value.map(toPlainValue);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, toPlainValue(v)]));
  return value;
}

async function call(baseUrl, path, token, headers = {}) {
  const response = await fetch(baseUrl + path, {
    headers: { "content-type": "application/json", ...(token ? { "x-hydra-access-token": token } : {}), ...headers },
  });
  const text = await response.text();
  let body = text;
  try {
    body = JSON.parse(text);
  } catch {}
  return { status: response.status, contentType: response.headers.get("content-type"), length: Buffer.byteLength(text), body };
}

// ---------------------------------------------------------------------------------------------------------------
// replay: real data. REF_SNAPSHOT_URI (e.g. mongodb://127.0.0.1:27018/test, a restored prod dump; only read) is copied
// into the scratch database (playertesters, friendlists, friendrequests, with their indexes); then every account (or
// REF_SAMPLE of them, evenly spread) gets the four reads in the login's order, then the two profile lookups for its
// friends. dataassets is copied too (the icons), and the TS server told to reload them. Recorded: every answer as sent (the text,
// compared byte for byte), then the lists created, the player documents that changed, and Redis. Write commands are
// not recorded (the profiler would drop most of them); `run` covers those.

async function replay(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("friends_diff");
  const secret = need("REF_JWT_SECRET");
  const snapshotClient = new MongoClient(need("REF_SNAPSHOT_URI"), { appName: "friends_diff" });
  await snapshotClient.connect();
  const snapshot = snapshotClient.db();
  const collections = ["playertesters", "friendlists", "friendrequests", "dataassets"];
  const original = {};
  for (const name of collections) {
    const docs = await snapshot.collection(name).find().toArray();
    if (docs.length > 0) await db.collection(name).insertMany(docs, { ordered: true });
    for (const index of await snapshot.collection(name).indexes()) {
      if (index.name === "_id_") continue;
      const { key, v, ns, ...options } = index;
      await db.collection(name).createIndex(key, options);
    }
    original[name] = new Map(docs.map((d) => [d._id.toString(), EJSON.stringify(d, { relaxed: false })]));
    console.log(`${name}: ${docs.length} copied`);
  }
  await snapshotClient.close();

  const anIcon = await db.collection("dataassets").findOne({ assetType: "ProfileIconData" });
  if (anIcon) await reloadAssets(baseUrl, { assetType: anIcon.assetType, slug: anIcon.slug, assetPath: anIcon.assetPath, character_slug: anIcon.character_slug, enabled: anIcon.enabled });
  const friendIds = new Map((await db.collection("friendlists").find().toArray()).map((l) =>
    [String(l.accountId), (l.friends || []).filter((f) => (f.status ?? "active") === "active").map((f) => f.friendAccountId)]));
  const ids = new Set(original.playertesters.keys());
  for (const doc of await db.collection("friendlists").find({}, { projection: { accountId: 1 } }).toArray()) ids.add(String(doc.accountId));
  for (const doc of await db.collection("friendrequests").find({}, { projection: { fromAccountId: 1, toAccountId: 1 } }).toArray()) {
    ids.add(String(doc.fromAccountId));
    ids.add(String(doc.toAccountId));
  }
  let accounts = [...ids].sort();
  const sample = Number(process.env.REF_SAMPLE || 0);
  if (sample > 0 && sample < accounts.length) accounts = accounts.filter((_, i) => i % Math.ceil(accounts.length / sample) === 0);
  console.log(`${accounts.length} accounts`);

  const routes = ["/friends/me/invitations/incoming?page_size=1000&state=open&", "/friends/me/invitations/outgoing?page_size=1000&state=open&", "/friends/me?page_size=1000&", "/social/me/blocked"];
  const answers = new Array(accounts.length);
  let next = 0;
  const worker = async () => {
    while (next < accounts.length) {
      const i = next++;
      const token = jwt.sign({ id: accounts[i], username: "replay" }, secret);
      const results = [];
      for (const route of routes) {
        const response = await fetch(baseUrl + route, { headers: { "content-type": "application/json", "x-hydra-access-token": token } });
        results.push({ status: response.status, contentType: response.headers.get("content-type"), text: await response.text() });
      }
      // The lookups the friends list makes next, with this account's friends (the game asks for both).
      const friends = friendIds.get(accounts[i]);
      if (friends?.length) {
        for (const path of ["/accounts/wb_network/bulk", "/profiles/bulk"]) results.push(await callHydra(baseUrl, path, { ids: friends }, token));
      }
      answers[i] = { name: accounts[i], response: results };
    }
  };
  await Promise.all(Array.from({ length: 16 }, worker));

  // Lists by account (a new list's _id is made up, and made at different moments by the two runs); players that changed.
  const lists = {};
  for (const doc of await db.collection("friendlists").find().toArray()) {
    const created = !original.friendlists.has(doc._id.toString());
    lists[String(doc.accountId)] = JSON.parse(EJSON.stringify(created ? { ...doc, _id: "<created>" } : doc, { relaxed: false }));
  }
  const players = {};
  for (const doc of await db.collection("playertesters").find().toArray()) {
    const text = EJSON.stringify(doc, { relaxed: false });
    if (original.playertesters.get(doc._id.toString()) !== text) players[doc._id.toString()] = JSON.parse(text);
  }
  const state = await dump(redis, { listCollections: () => ({ toArray: async () => [] }) });
  // Sorted: new lists land in the order the concurrent requests happened to make them.
  const sorted = (o) => Object.fromEntries(Object.entries(o).sort(([x], [y]) => (x < y ? -1 : x > y ? 1 : 0)));
  const steps = [...answers, { name: "state", response: null, state: { lists: sorted(lists), players: sorted(players), redis: state.redis } }];
  console.log(`${Object.keys(players).length} players changed, ${Object.values(lists).filter((l) => l._id === "<created>").length} lists created`);
  writeRun(outFile, baseUrl, Date.now(), steps, undefined);
  await close();
}
