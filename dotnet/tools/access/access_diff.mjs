// Key-for-key comparison of /access between two servers: the TS server (the reference) and the C# one.
// Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/access/access_diff.mjs run <baseUrl> <out.json>   # drives the scenarios, dumps every write
//   node dotnet/tools/access/access_diff.mjs diff <ts.json> <cs.json>   # what differs, after normalizing
//
// Both servers must use the same scratch stores, which `run` wipes first:
//   REF_REDIS_URL   e.g. redis://default:pw@127.0.0.1:16390 (a throwaway Redis: FLUSHDB)
//   REF_MONGO_URI   e.g. mongodb://127.0.0.1:27017/ovs_access_ref (a scratch database: dropped)
//   REF_ACCESS_BODY a captured /access request body (Hydra), e.g. dotnet/local/hydra-corpus/req/...__access.bin
//   REF_BANNED_IP   an IP both servers have in their IP ban file (default 198.51.100.66)
//   REF_JWT_SECRET  the JWT secret both servers use (enables the identify-token scenario)
// Never point these at data you want to keep.
import fs from "node:fs";
import { require, need, openScratch, readProfile, toPlain, dump, writeRun, diff } from "../refdiff/refdiff.mjs";

const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
// The package entry runs a CLI on import (it opens argv[2]); the decoder module alone does not.
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diff(args[0], args[1]);
else {
  console.error("usage: access_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}

// ---------------------------------------------------------------------------------------------------------------
// run

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("access_diff");

  const body = fs.readFileSync(need("REF_ACCESS_BODY"));
  const bannedIp = process.env.REF_BANNED_IP || "198.51.100.66";
  const STEAM_A = "76561198000000001", STEAM_OLD = "76561198000000077", STEAM_C = "76561198000000003";
  const INSTALL_A = "0123456789abcdef0123456789abcdef", INSTALL_B = "fedcba9876543210fedcba9876543210";
  const EPIC_B = "aaaabbbbccccddddeeeeffff00001111";
  const identity = (fields) => ({ steamId: "", epicId: "", hardwareId: "", hardwareIdVersion: "", hardwareIdQuality: "", installId: "", clientVersion: "2026.09.28.04", identityRegistered: "1", ...fields });
  const steps = [];
  const post = async (name, ip, headers = {}) => record(name, await call(baseUrl, "POST", ip, body, headers));
  const record = async (name, response) => steps.push({ name, response, state: await dump(redis, db) });

  // 1. A new player, identified by /api/identify's IP record.
  await redis.hSet("identity:198.51.100.1", identity({ steamId: STEAM_A, installId: INSTALL_A }));
  await redis.expire("identity:198.51.100.1", 300);
  await post("identified-new", "198.51.100.1");
  const first = steps[0].response;
  const idA = first.body?.account?.id;

  // 2. The same player again, with match stats, a party key and a stale ranked set to clean up.
  if (idA) {
    await redis.set(`player_ranked_set:${idA}`, "set-1");
    await redis.set(`ranked_disconnect:${idA}`, "1");
    await db.collection("playertesters").updateOne({ _id: new ObjectId(idA) }, { $set: { party_key: "AbCd" } });
    await db.collection("eloratings").insertOne({ account_id: idA, wins_1v1: 3, wins_2v2: 1, losses_1v1: 2, losses_2v2: 0 });
    await db.collection("playerstats").insertOne({
      account_id: idA,
      characters_1v1: { character_Jason: { wins: 2, losses: 1, ringouts: 5, totalDamageDealt: 100.4, highestDamageDealt: 55.5 } },
      characters_2v2: {
        character_Jason: { wins: 1, losses: 0, ringouts: 1, totalDamageDealt: 0.2, highestDamageDealt: 60 },
        character_C018: { wins: 0, losses: 2, ringouts: 0, totalDamageDealt: 2.5, highestDamageDealt: 1.25 },
      },
    });
  }
  await post("identified-again", "198.51.100.1");

  // 3. The same player from another IP, identified by the token the first login returned.
  await post("jwt-new-ip", "198.51.100.2", first.body?.token ? { "x-hydra-access-token": first.body.token } : {});

  // 4-5. An identity-less login (waits 3 s for a late identify, then gets a provisional account), then again.
  await post("identity-less-new", "198.51.100.3");
  await post("identity-less-again", "198.51.100.3");

  // 6. Adoption: an id-less account on the IP is taken over by a newly identified client.
  await db.collection("playertesters").insertOne({
    _id: new ObjectId("0000000000000000000a0006"), name: "Legacy Six", hydraUsername: "legacy-six", ip: "198.51.100.4",
    GameplayPreferences: 964, profile_id: new ObjectId("0000000000000000000b0006"), public_id: "00000000-0000-4000-8000-000000000006",
    profile_icon: "profile_icon_default", blockedPlayers: ["0000000000000000000a0099"], character: "character_shaggy",
    variant: "skin_shaggy_default", party_key: "", steamId: "", epicId: "", hardwareId: "", hardwareIdVersion: "",
    hardwareIdQuality: "", installId: "", lastSeenAt: new Date("2026-09-01T00:00:00Z"), ipSeenAt: new Date("2026-09-01T00:00:00Z"),
    provisional: false, __v: 0,
  });
  await redis.hSet("identity:198.51.100.4", identity({ epicId: EPIC_B, installId: INSTALL_B }));
  await post("adoption", "198.51.100.4");

  // 7. The IP rule: a new identified player releases an inactive, reachable account's link to the IP.
  await db.collection("playertesters").insertOne({
    _id: new ObjectId("0000000000000000000a0007"), name: "Old Seven", hydraUsername: "old-seven", ip: "198.51.100.5",
    GameplayPreferences: 964, profile_id: new ObjectId("0000000000000000000b0007"), public_id: "00000000-0000-4000-8000-000000000007",
    profile_icon: "profile_icon_default", blockedPlayers: [], character: "character_shaggy", variant: "skin_shaggy_default",
    party_key: "", steamId: STEAM_OLD, epicId: "", hardwareId: "", hardwareIdVersion: "", hardwareIdQuality: "",
    installId: "", lastSeenAt: new Date("2026-08-01T00:00:00Z"), ipSeenAt: new Date("2026-08-01T00:00:00Z"), provisional: false, __v: 0,
  });
  await redis.hSet("identity:198.51.100.5", identity({ steamId: STEAM_C }));
  await post("stale-ip-release", "198.51.100.5");

  // 8. An account from before the identity work (no installId, hardware version/quality, lastSeenAt, ipSeenAt,
  // provisional): adopted by a newly identified client, so every missing field gets its default in one save.
  await db.collection("playertesters").insertOne({
    _id: new ObjectId("0000000000000000000a0008"), name: "Old Timer", hydraUsername: "old-timer", ip: "198.51.100.8",
    token: { id: "0000000000000000000a0008", username: "Old Timer" }, account: { id: "0000000000000000000a0008", username: "Old Timer" },
    GameplayPreferences: 964, profile_id: new ObjectId("0000000000000000000b0008"), public_id: "00000000-0000-4000-8000-000000000008",
    profile_icon: "profile_icon_default", blockedPlayers: [], character: "character_shaggy", variant: "skin_shaggy_default",
    party_key: "", steamId: "", epicId: "", hardwareId: "", __v: 0,
  });
  await redis.hSet("identity:198.51.100.8", identity({ steamId: "76561198000000008", installId: "88888888888888888888888888888888" }));
  await post("pre-identity-account", "198.51.100.8");

  // 9. A stored placeholder Steam id ("Unknown"): found by its Epic id, the real Steam id replaces it.
  await db.collection("playertesters").insertOne({
    _id: new ObjectId("0000000000000000000a0009"), name: "Placeholder Nine", hydraUsername: "placeholder-nine", ip: "198.51.100.9",
    GameplayPreferences: 964, profile_id: new ObjectId("0000000000000000000b0009"), public_id: "00000000-0000-4000-8000-000000000009",
    profile_icon: "profile_icon_default", blockedPlayers: [], character: "character_shaggy", variant: "skin_shaggy_default",
    party_key: "", steamId: "Unknown", epicId: "99999999999999999999999999999999", hardwareId: "", hardwareIdVersion: "",
    hardwareIdQuality: "", installId: "", lastSeenAt: new Date("2026-09-20T00:00:00Z"), ipSeenAt: new Date("2026-09-20T00:00:00Z"),
    provisional: false, __v: 0,
  });
  await redis.hSet("identity:198.51.100.9", identity({ steamId: "76561198000000009", epicId: "99999999999999999999999999999999" }));
  await post("placeholder-steam-id", "198.51.100.9");

  // 10. An identify-style token (claims written by /api/identify, hardwareIdVersion a number) with a strong fingerprint.
  if (process.env.REF_JWT_SECRET) {
    const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
    const token = jwt.sign({
      id: "", steamId: "76561198000000010", epicId: "", installId: "10101010101010101010101010101010",
      hardwareId: "ab".repeat(32), hardwareIdVersion: 2, hardwareIdQuality: "Strong", clientVersion: "2026.09.28.04", identityRegistered: "1",
    }, process.env.REF_JWT_SECRET);
    await post("identify-token-hardware", "198.51.100.10", { "x-hydra-access-token": token });
  }

  // 11. A banned IP: nothing is written, the answer is empty.
  await redis.hSet(`identity:${bannedIp}`, identity({ steamId: "76561198000000066" }));
  await post("banned", bannedIp);

  // 12. Logout.
  await record("delete", await call(baseUrl, "DELETE", "198.51.100.1", null, {}));

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "access_diff"));
  await close();
}

async function call(baseUrl, method, ip, body, headers) {
  const response = await fetch(baseUrl + "/access", {
    method,
    headers: { "x-real-ip": ip, ...(body ? { "content-type": "application/x-ag-binary" } : {}), ...headers },
    body: body ?? undefined,
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  let decoded = null;
  if (bytes.length > 0 && (response.headers.get("content-type") || "").includes("x-ag-binary")) {
    decoded = toPlain(new HydraDecoder(bytes).readValue());
  }
  return { status: response.status, contentType: response.headers.get("content-type"), length: bytes.length, body: decoded };
}
