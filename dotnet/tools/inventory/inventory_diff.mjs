// Key-for-key comparison of GET /profiles/{id}/inventory between two servers: the TS server (the reference) and the C#
// one. The answer (every unlockAll item, Gleamium, the toast record) and every write (the player's playercounters
// document; stale active_ip_accounts entries removed while resolving the player). The scenarios cover each step of
// the TS server's resolveAccountFromRequest: the token's id, the Steam index, an id-less token with a header, the IP.
// Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/inventory/inventory_diff.mjs run <baseUrl> <out.json>
//   node dotnet/tools/inventory/inventory_diff.mjs diff <ts.json> <cs.json>
//
// Environment as tools/friends/friends_diff.mjs: REF_REDIS_URL, REF_MONGO_URI, REF_JWT_SECRET, REF_DATA_ASSET_TOKEN,
// REF_PROFILE=1 (needed: diff compares the writes). Item ids are new on every request on both servers; the diff
// numbers them in order of appearance, so an order difference shows as many differences: read the first.
import { require, need, openScratch, readProfile, dump, writeRun, diff, reloadAssets } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
process.argv = argv;

const [, , command, ...args] = process.argv;
if (command === "run") await run(args[0], args[1]);
else if (command === "diff") diff(args[0], args[1], { writes: true });
else {
  console.error("usage: inventory_diff.mjs run <baseUrl> <out.json> | diff <a.json> <b.json>");
  process.exit(2);
}

function oid(n) {
  return `0000000000000000000a${String(n).padStart(4, "0")}`;
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("inventory_diff");
  const secret = need("REF_JWT_SECRET");
  const steps = [];
  const get = async (name, claims, { headers = {}, ip = "198.51.100.200" } = {}) => {
    const token = jwt.sign(claims, secret);
    const response = await fetch(baseUrl + `/profiles/${oid(999)}/inventory`, {
      method: "PUT",
      headers: { "content-type": "application/x-ag-binary", "x-hydra-http-method": "GET", "x-hydra-access-token": token, "x-real-ip": ip, ...headers },
      body: Buffer.from([0x60, 0x00]),
    });
    const bytes = Buffer.from(await response.arrayBuffer());
    const body = bytes.length && (response.headers.get("content-type") || "").includes("x-ag-binary") ? plain(new HydraDecoder(bytes).readValue()) : bytes.toString();
    steps.push({ name, response: { status: response.status, length: bytes.length, body }, state: await dump(redis, db, ["config", "dataassets"]) });
  };

  // Assets: two characters, a test character and a test character's skin (both left out), two perks (one disabled),
  // an icon. The TS server reads them at startup; it is told to reload.
  const now = new Date();
  const asset = (assetType, slug, fields = {}) => ({ assetType, slug, assetPath: `/Game/${slug}`, character_slug: "", enabled: true, createdAt: now, updatedAt: now, __v: 0, ...fields });
  await db.collection("dataassets").insertMany([
    asset("CharacterData", "character_a"),
    asset("CharacterData", "character_Meeseeks"),
    asset("SkinData", "skin_a_1", { character_slug: "character_a" }),
    asset("SkinData", "skin_manny_1", { character_slug: "character_manny" }),
    asset("CharacterData", "character_b"),
    asset("MvsPerkHsda", "perk_one"),
    asset("MvsPerkHsda", "perk_off", { enabled: false }),
    asset("ProfileIconData", "profile_icon_default"),
  ]);
  await reloadAssets(baseUrl, { assetType: "ProfileIconData", slug: "profile_icon_default", assetPath: "/Game/profile_icon_default", character_slug: "", enabled: true });

  const connection = (id, fields = {}) => ({ id, username: `Player ${id}`, ...fields });
  const STEAM = "76561198000000501", EPIC = "abcdefabcdefabcdefabcdefabcdef01";

  // 1. The token's id has a session whose own id differs from the key: that id is the player.
  await redis.hSet(`connections:${oid(501)}`, connection(oid(502)));
  await get("token-session", { id: oid(501) });
  // 2. Again: the counters document exists now; only updatedAt is written.
  await get("token-session-again", { id: oid(501) });

  // 3. No session for the token's id; its Steam id is indexed.
  await redis.set(`identity:steam:${STEAM}`, oid(503));
  await redis.hSet(`connections:${oid(503)}`, connection(oid(503)));
  await get("steam-index", { id: oid(504), steamId: STEAM });

  // 4. A token with no id (as /api/identify issues): its fields are not read; the header's Steam id is.
  await redis.set(`identity:epic:${EPIC}`, oid(505));
  await redis.hSet(`connections:${oid(505)}`, connection(oid(505)));
  await get("id-less-token-header", { id: "", epicId: EPIC }, { headers: { "x-steam-id": STEAM } });

  // 5. The IP: exactly one player active there (the token's current_ip, ::ffff: form).
  await redis.zAdd("active_ip_accounts:198.51.100.7", { score: Date.now(), value: oid(506) });
  await redis.hSet(`connections:${oid(506)}`, connection(oid(506)));
  await get("ip-one-active", { id: oid(507), current_ip: "::ffff:198.51.100.7" });

  // 6. Two players active at the request's IP: not resolved, the token's id is the player.
  await redis.zAdd("active_ip_accounts:198.51.100.8", [{ score: Date.now(), value: oid(508) }, { score: Date.now(), value: oid(509) }]);
  await get("ip-two-active", { id: oid(510) }, { ip: "198.51.100.8" });

  // 7. One stale entry at the request's IP (older than 90 s): removed, not resolved.
  await redis.zAdd("active_ip_accounts:198.51.100.9", { score: Date.now() - 600_000, value: oid(511) });
  await get("ip-stale", { id: oid(512) }, { ip: "198.51.100.9" });

  // 8. A session hash without an id field does not count.
  await redis.hSet(`connections:${oid(513)}`, { username: "No Id" });
  await get("session-without-id", { id: oid(513) });

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "inventory_diff"));
  await close();
}

function plain(value) {
  if (typeof value === "bigint") return `<bigint:${value}>`;
  if (typeof value === "number" && Number.isNaN(value)) return "<NaN>";
  if (Array.isArray(value)) return value.map(plain);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, plain(v)]));
  return value;
}
