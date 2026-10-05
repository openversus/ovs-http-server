// GET and PUT /ssc/invoke/hiss_amalgamation on the TS server and the C# port. Run from the repository root:
//
//   REF_JWT_SECRET=<both servers' JWT_SECRET> [REF_DATA_ASSET_TOKEN=<the TS server's DATA_ASSET_TOKEN> REF_MONGO_URI=...] \
//     node dotnet/tools/hiss/hiss_diff.mjs <tsUrl> <csUrl>
//
// Each answer is asked for as GET and as PUT (a Crc in the body, which neither server reads), in Hydra and in JSON.
// JSON must be the same bytes. In Hydra the compressed sections are compared by what they hold, not by their bytes: the
// C# port compresses with .NET's zlib at its optimal level, the TS server with Node's at its fastest, and the game
// inflates either. So each answer is split into its compressed sections (0x67, index 1, a byte string of zlib data) and
// the rest: the rest must be the same bytes (where only a section's length is written differently: its byte-string
// header is left out), and each section must inflate to the same bytes (a byte comparison, so key order counts).
//
// With REF_ZSTD_VERSION (the C# server's Hiss:ZstdMinimumVersion), each is asked for by three clients: one stating no
// version and one below that version (both must get zlib sections, as now), and one at it (it must get zstd sections,
// the same 20 once unpacked). The client's version travels in its token's clientVersion claim, which the client gate
// reads when there is no connection record; every request comes from a test address (198.18.0.0/15) of its own client,
// so no real player's record is found by IP.
//
// With REF_DATA_ASSET_TOKEN and REF_MONGO_URI, the asset sync runs too: one skin is disabled through the TS server's
// POST /syncAsset (it reloads its assets and bumps the CRC), both answers compared again (the C# port must have rebuilt
// for the new CRC, without that skin), then the skin is enabled again the same way and compared once more. That writes
// the store: the skin's document ends as it began (its updatedAt, which the sync stamps, is set back), the CRC ends two
// higher.
import { hydraSections, require } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const [, , tsUrl, csUrl] = argv;
if (!tsUrl || !csUrl || !process.env.REF_JWT_SECRET) {
  console.error("usage: REF_JWT_SECRET=... hiss_diff.mjs <tsUrl> <csUrl>");
  process.exit(2);
}

const HYDRA = "application/x-ag-binary";
const zstdVersion = process.env.REF_ZSTD_VERSION;
const clients = [
  { name: "stock", claims: {}, ip: "198.18.200.1", zstd: false },
  ...(zstdVersion ? [
    { name: "older", claims: { clientVersion: "2000.1.1" }, ip: "198.18.200.2", zstd: false },
    { name: "zstd", claims: { clientVersion: zstdVersion }, ip: "198.18.200.3", zstd: true },
  ] : []),
].map((c, i) => ({ ...c, token: jwt.sign({ id: `0000000000000000000a000${i + 1}`, ...c.claims }, process.env.REF_JWT_SECRET) }));

async function ask(base, method, hydra, client = clients[0]) {
  const headers = { "x-hydra-access-token": client.token, "x-real-ip": client.ip };
  let body;
  if (method === "PUT") {
    if (hydra) {
      const encoder = new HydraEncoder();
      encoder.encodeValue({ Crc: 1 });
      body = encoder.returnValue();
    } else {
      body = JSON.stringify({ Crc: 1 });
    }
    headers["content-type"] = hydra ? HYDRA : "application/json";
  } else if (hydra) {
    headers["content-type"] = HYDRA;
  }
  const response = await fetch(`${base}/ssc/invoke/hiss_amalgamation`, { method, headers, body, signal: AbortSignal.timeout(60000) });
  return { status: response.status, type: (response.headers.get("content-type") ?? "").split(";")[0], bytes: Buffer.from(await response.arrayBuffer()) };
}

let compared = 0;
const problems = [];
async function compare(label) {
  for (const client of clients) for (const method of ["GET", "PUT"]) {
    for (const hydra of [true, false]) {
      const [ts, cs] = [await ask(tsUrl, method, hydra, client), await ask(csUrl, method, hydra, client)];
      const name = `${label} ${client.name} ${method} ${hydra ? "hydra" : "json"}`;
      compared++;
      if (ts.status !== 200 || ts.status !== cs.status || ts.type !== cs.type) {
        problems.push(`${name}: ${ts.status} ${ts.type} vs ${cs.status} ${cs.type}`);
        continue;
      }
      if (!hydra) {
        if (!ts.bytes.equals(cs.bytes)) problems.push(`${name}: ${ts.bytes.length} vs ${cs.bytes.length} bytes`);
        continue;
      }
      const [a, b] = [hydraSections(ts.bytes), hydraSections(cs.bytes)];
      if (a.sections.length !== 20 || b.sections.length !== 20) problems.push(`${name}: ${a.sections.length} vs ${b.sections.length} compressed sections (20 expected)`);
      if (!a.rest.equals(b.rest)) problems.push(`${name}: the bytes outside the sections differ`);
      if (b.zstd !== (client.zstd ? 20 : 0)) problems.push(`${name}: ${b.zstd} zstd sections, ${client.zstd ? 20 : 0} expected`);
      a.sections.forEach((s, i) => {
        if (!b.sections[i]?.equals(s)) problems.push(`${name}: section ${i} holds ${s.length} vs ${b.sections[i]?.length} bytes, not the same`);
      });
      if (label === "start" && method === "PUT" && client.name !== "older") {
        console.log(`  ${name}: TS ${ts.bytes.length} bytes, C# ${cs.bytes.length} bytes; ${a.sections.reduce((n, s) => n + s.length, 0)} bytes inflated`);
      }
    }
  }
}

// The C# server makes the zstd answer in the background after the zlib one: until it is ready, everyone gets zlib.
async function zstdReady() {
  const client = clients.find((c) => c.zstd);
  for (let i = 0; client && i < 60; i++) {
    if (hydraSections((await ask(csUrl, "PUT", true, client)).bytes).zstd > 0) return;
    await new Promise((r) => setTimeout(r, 500));
  }
}

await zstdReady();
await compare("start");

if (process.env.REF_DATA_ASSET_TOKEN && process.env.REF_MONGO_URI) {
  const { MongoClient } = require(process.cwd() + "/node_modules/mongodb");
  const mongo = new MongoClient(process.env.REF_MONGO_URI);
  await mongo.connect();
  const assets = mongo.db().collection("dataassets");
  const config = mongo.db().collection("config");
  const skin = await assets.findOne({ assetType: "SkinData", enabled: true, character_slug: { $type: "string" } });
  const crcBefore = (await config.findOne())?.CRC;
  const sync = async (enabled) => {
    const response = await fetch(`${tsUrl}/syncAsset`, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bearer ${process.env.REF_DATA_ASSET_TOKEN}` },
      body: JSON.stringify({ assetType: skin.assetType, assetPath: skin.assetPath, slug: skin.slug, enabled, character_slug: skin.character_slug }),
    });
    if (!response.ok) throw new Error(`syncAsset answered ${response.status}`);
    // The TS server reloads its assets and bumps the CRC after answering.
    await new Promise((r) => setTimeout(r, 1000));
  };
  const csAnswer = async () => JSON.parse((await ask(csUrl, "PUT", false)).bytes);
  const listed = (answer) => answer.body.Data["enabled-assets-data"]._hydra_compressed.ClientAssetData.DefaultVisibleAssets
    .SkinSlugsByCharacter[skin.character_slug]?.Slugs.includes(skin.slug);
  try {
    await sync(false);
    const disabled = await csAnswer();
    console.log(`  disabled ${skin.slug}: CRC ${crcBefore} -> ${disabled.body.Crc} (C#), still listed by C#: ${listed(disabled)}`);
    if (disabled.body.Crc === crcBefore || listed(disabled)) problems.push(`sync: the C# answer was not rebuilt (CRC ${disabled.body.Crc}, skin listed: ${listed(disabled)})`);
    await zstdReady();
    await compare("disabled");
  } finally {
    await sync(true);
    await assets.updateOne({ _id: skin._id }, { $set: { updatedAt: skin.updatedAt } });
  }
  const enabled = await csAnswer();
  if (!listed(enabled)) problems.push("sync: the re-enabled skin is missing from the C# answer");
  await zstdReady();
  await compare("enabled again");
  const after = await assets.findOne({ _id: skin._id });
  if (JSON.stringify(after) !== JSON.stringify(skin)) problems.push(`sync: ${skin.slug}'s document did not end as it began`);
  console.log(`  CRC now ${(await config.findOne())?.CRC}`);
  await mongo.close();
}

console.log(`${compared} answers compared: ${problems.length} differences`);
for (const p of problems.slice(0, 30)) console.log("  " + p);
process.exit(problems.length ? 1 : 0);
