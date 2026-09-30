// The six cosmetics writes (PUT /ssc/invoke/equip_taunt, equip_stat_tracker, equip_announcer_pack, equip_banner,
// equip_ringout_vfx, set_profile_icon) on the TS server and the C# port. Run from the repository root (it uses the TS
// server's node_modules):
//
//   node dotnet/tools/cosmetics/equip_diff.mjs run <baseUrl> <out.json>     # scenarios on scratch stores
//   node dotnet/tools/cosmetics/equip_diff.mjs diff <ts.json> <cs.json>
//
// run: REF_REDIS_URL, REF_MONGO_URI (scratch; wiped), REF_JWT_SECRET, REF_PROFILE=1, REF_DATA_ASSET_TOKEN (the TS
// server reads dataassets at startup; POST /syncAsset makes it reload them after seeding). Every step records the answer
// (status and bytes, JSON or Hydra as asked) and the state of both stores after it; diff compares them and the Mongo
// updates and findAndModify commands each server sent. Restart the TS server before each run (see the last step).
//
// One expected difference (2 lines), announcer-missing-slug-hydra: a Hydra request without AnnouncerPackSlug gets
// EquippedAnnouncerPack NaN from the TS server (its Hydra encoder's form of undefined) and no key from the C# port; the
// JSON answers agree (JSON.stringify drops undefined). The game always sends the slug.
import { require, need, openScratch, readProfile, dump, writeRun, diff, toPlain, reloadAssets } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const { ObjectId } = require(process.cwd() + "/node_modules/mongodb");
// mvs-dump's modules run a CLI on import when argv[2] is set (they read it as a file): hide ours while they load.
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraDecoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/decoder.js");
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const oid = (n) => "0000000000000000000e" + n.toString(16).padStart(4, "0");

async function call(baseUrl, route, token, body, hydra) {
  let payload = JSON.stringify(body);
  if (hydra) {
    const encoder = new HydraEncoder();
    encoder.encodeValue(body);
    payload = encoder.returnValue();
  }
  const response = await fetch(baseUrl + "/ssc/invoke/" + route, {
    method: "PUT",
    headers: { "x-hydra-access-token": token, "x-real-ip": "198.51.100.9", "content-type": hydra ? "application/x-ag-binary" : "application/json" },
    body: payload,
    signal: AbortSignal.timeout(15000),
  }).catch((e) => ({ status: `<no answer: ${e.name}>`, arrayBuffer: async () => new ArrayBuffer(0) }));
  const bytes = Buffer.from(await response.arrayBuffer());
  let value = null;
  try {
    value = bytes.length ? toPlain(hydra ? new HydraDecoder(bytes).readValue() : JSON.parse(bytes.toString())) : null;
  } catch {
    value = `<unparsed: ${bytes.toString()}>`;
  }
  return { status: response.status, bytes: bytes.toString("base64"), value };
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("equip_diff");
  const asset = (assetType, slug, character_slug = "", enabled = true) => ({ assetType, slug, character_slug, enabled, assetPath: `/Game/${assetType}/${slug}` });
  await db.collection("dataassets").insertMany([
    asset("CharacterData", "character_shaggy"), asset("CharacterData", "character_taz"),
    asset("CharacterData", "character_C022"), // a test character: left out
    asset("TauntData", "taunt_shaggy_1", "character_shaggy"), asset("TauntData", "taunt_shaggy_2", "character_shaggy"),
    asset("TauntData", "taunt_taz_1", "character_taz"),
    asset("ProfileIconData", "profile_icon_default"), asset("ProfileIconData", "profile_icon_bat"),
    asset("ProfileIconData", "profile_icon_off", "", false), // disabled: unknown to the server
  ]);
  await reloadAssets(baseUrl, await db.collection("dataassets").findOne({ slug: "character_shaggy" }));

  const player = (n) => ({ _id: new ObjectId(oid(n)), name: `p${n}`, profile_icon: "profile_icon_default", __v: 0 });
  await db.collection("playertesters").insertMany([1, 2, 3, 4, 5, 6, 7].map(player));
  // 2: a stored document, no cache. 3: a stored document and its cache (as get_equipped_cosmetics leaves them).
  // 4: a cache with no stored document. 5: a cache without Taunts. 1, 6, 7: nothing.
  const stored = (n) => ({
    _id: new ObjectId(oid(n)), account_id: new ObjectId(oid(n)),
    Taunts: { character_taz: { TauntSlots: ["mine", "", "", ""] } },
    AnnouncerPack: "announcer_x", Banner: "banner_x", StatTrackers: { StatTrackerSlots: ["a", "b", "c"] }, RingoutVfx: "vfx_x",
    Gems: { GemSlots: ["g", "", ""] }, __v: 0,
  });
  await db.collection("cosmetics").insertMany([stored(2), stored(3)]);
  const cache = (n, extra = {}) => JSON.stringify({
    _id: oid(n), account_id: oid(n),
    Taunts: { character_shaggy: { TauntSlots: ["taunt_shaggy_1", "", "", ""] }, character_taz: { TauntSlots: ["mine", "", "", ""] } },
    AnnouncerPack: "announcer_x", Banner: "banner_x", StatTrackers: { StatTrackerSlots: ["a", "b", "c"] }, RingoutVfx: "vfx_x",
    Gems: { GemSlots: ["g", "", ""] }, __v: 0, ...extra,
  });
  await redis.set(`player:${oid(3)}:cosmetics`, cache(3));
  await redis.set(`player:${oid(4)}:cosmetics`, cache(4));
  const noTaunts = JSON.parse(cache(5));
  delete noTaunts.Taunts;
  await redis.set(`player:${oid(5)}:cosmetics`, JSON.stringify(noTaunts));

  const secret = need("REF_JWT_SECRET");
  const token = (id) => jwt.sign({ id }, secret);
  const steps = [];
  const step = async (name, route, id, body, hydra = false) => {
    const response = await call(baseUrl, route, token(id), body, hydra);
    steps.push({ name, response, state: await dump(redis, db, ["dataassets", "config"]) });
  };
  const taunt = (CharacterSlug, TauntSlotIndex, TauntSlug) => ({ CharacterSlug, TauntSlotIndex, TauntSlug });

  // equip_taunt reads the cached cosmetics first (creating them when there is neither cache nor document).
  await step("taunt-nothing-stored", "equip_taunt", oid(1), taunt("character_shaggy", 1, "taunt_shaggy_2"));
  await step("taunt-cached-after", "equip_taunt", oid(1), taunt("character_taz", 0, "taunt_taz_1"));
  await step("taunt-document-no-cache", "equip_taunt", oid(2), taunt("character_shaggy", 2, "taunt_shaggy_1"));
  await step("taunt-cached", "equip_taunt", oid(3), taunt("character_taz", 3, "taunt_taz_1"), true);
  await step("taunt-cache-no-document", "equip_taunt", oid(4), taunt("character_shaggy", 0, "taunt_shaggy_2"));
  await step("taunt-unknown-character", "equip_taunt", oid(3), taunt("character_nobody", 0, "x"));
  await step("taunt-slot-past-end", "equip_taunt", oid(3), taunt("character_taz", 5, "taunt_taz_1"));
  await step("taunt-bad-id", "equip_taunt", "not-an-object-id", taunt("character_shaggy", 0, "x"));

  // equip_stat_tracker reads the stored document, not the cache.
  await step("stat-nothing-stored", "equip_stat_tracker", oid(6), { StatTrackerSlotIndex: 1, StatTrackerSlug: "st_1" });
  await step("stat-document", "equip_stat_tracker", oid(2), { StatTrackerSlotIndex: 2, StatTrackerSlug: "st_2" }, true);
  await step("stat-past-end", "equip_stat_tracker", oid(3), { StatTrackerSlotIndex: 4, StatTrackerSlug: "st_4" });
  await step("stat-missing-slug", "equip_stat_tracker", oid(3), { StatTrackerSlotIndex: 0 });
  await step("stat-cache-no-document", "equip_stat_tracker", oid(4), { StatTrackerSlotIndex: 0, StatTrackerSlug: "st_0" });

  await step("announcer-nothing-stored", "equip_announcer_pack", oid(7), { AnnouncerPackSlug: "announcer_a" });
  await step("announcer-document", "equip_announcer_pack", oid(2), { AnnouncerPackSlug: "announcer_b" }, true);
  await step("announcer-missing-slug", "equip_announcer_pack", oid(3), {});
  await step("announcer-missing-slug-hydra", "equip_announcer_pack", oid(3), {}, true);

  // equip_banner and equip_ringout_vfx read the equipped cosmetics first (creating them when there are none).
  await step("banner-nothing-stored", "equip_banner", oid(8), { BannerSlug: "banner_a" });
  await step("banner-document", "equip_banner", oid(3), { BannerSlug: "banner_b" }, true);
  await step("banner-empty", "equip_banner", oid(3), { BannerSlug: "" });
  await step("banner-cache-no-document", "equip_banner", oid(5), { BannerSlug: "banner_c" });
  await step("ringout-nothing-stored", "equip_ringout_vfx", oid(9), { RingoutVfxSlug: "vfx_a" });
  await step("ringout-document", "equip_ringout_vfx", oid(2), { RingoutVfxSlug: "vfx_b" }, true);
  await step("ringout-missing", "equip_ringout_vfx", oid(2), {});

  // set_profile_icon: only icons the game defines; the player record and the cosmetics.
  await step("icon-known", "set_profile_icon", oid(3), { Slug: "profile_icon_bat" }, true);
  await step("icon-nothing-stored", "set_profile_icon", oid(10), { Slug: "profile_icon_bat" });
  await step("icon-unknown", "set_profile_icon", oid(3), { Slug: "profile_icon_nope" });
  await step("icon-disabled", "set_profile_icon", oid(3), { Slug: "profile_icon_off" });
  await step("icon-not-a-string", "set_profile_icon", oid(3), { Slug: 5 });
  await step("icon-bad-id", "set_profile_icon", "not-an-object-id", { Slug: "profile_icon_bat" });

  // Last: TS assigns its module-level defaultTaunts to a cache without Taunts, and the slot write then changes the
  // defaults of every later new document in that process (the C# port copies them). Restart the TS server before a run.
  await step("taunt-cache-no-taunts", "equip_taunt", oid(5), taunt("character_shaggy", 1, "taunt_shaggy_2"));

  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "equip_diff"));
  await close();
}

const [, , command, a, b] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true, findAndModify: ["cosmetics"] });
else {
  console.error("usage: equip_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
