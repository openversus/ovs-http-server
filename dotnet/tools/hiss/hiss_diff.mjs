// GET and PUT /ssc/invoke/hiss_amalgamation on the TS server and the C# port. Run from the repository root:
//
//   REF_JWT_SECRET=<both servers' JWT_SECRET> [REF_DATA_ASSET_TOKEN=<the TS server's DATA_ASSET_TOKEN> REF_MONGO_URI=...] \
//     node dotnet/tools/hiss/hiss_diff.mjs <tsUrl> <csUrl>
//
// Each answer is asked for as GET and as PUT (a Crc in the body, which neither server reads), in Hydra and in JSON.
// JSON must be the same bytes. In Hydra the compressed sections are compared by what they hold, not by their bytes: the
// C# port compresses with .NET's zlib at its smallest size, the TS server with Node's at its fastest, and the game
// inflates either. So each answer is split into its compressed sections (0x67, index 1, a byte string of zlib data) and
// the rest: the rest must be the same bytes (where only a section's length is written differently: its byte-string
// header is left out), and each section must inflate to the same bytes (a byte comparison, so key order counts).
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
const token = jwt.sign({ id: "0000000000000000000a0001" }, process.env.REF_JWT_SECRET);

async function ask(base, method, hydra) {
  const headers = { "x-hydra-access-token": token };
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
  for (const method of ["GET", "PUT"]) {
    for (const hydra of [true, false]) {
      const [ts, cs] = [await ask(tsUrl, method, hydra), await ask(csUrl, method, hydra)];
      const name = `${label} ${method} ${hydra ? "hydra" : "json"}`;
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
      a.sections.forEach((s, i) => {
        if (!b.sections[i]?.equals(s)) problems.push(`${name}: section ${i} holds ${s.length} vs ${b.sections[i]?.length} bytes, not the same`);
      });
      if (label === "start" && method === "PUT") {
        console.log(`  ${name}: TS ${ts.bytes.length} bytes, C# ${cs.bytes.length} bytes; ${a.sections.reduce((n, s) => n + s.length, 0)} bytes inflated`);
      }
    }
  }
}

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
    await compare("disabled");
  } finally {
    await sync(true);
    await assets.updateOne({ _id: skin._id }, { $set: { updatedAt: skin.updatedAt } });
  }
  const enabled = await csAnswer();
  if (!listed(enabled)) problems.push("sync: the re-enabled skin is missing from the C# answer");
  await compare("enabled again");
  const after = await assets.findOne({ _id: skin._id });
  if (JSON.stringify(after) !== JSON.stringify(skin)) problems.push(`sync: ${skin.slug}'s document did not end as it began`);
  console.log(`  CRC now ${(await config.findOne())?.CRC}`);
  await mongo.close();
}

console.log(`${compared} answers compared: ${problems.length} differences`);
for (const p of problems.slice(0, 30)) console.log("  " + p);
process.exit(problems.length ? 1 : 0);
