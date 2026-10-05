// Puts the catalog rows in a manifest into a server's dataassets, through its POST /syncAsset (the TS server's, in
// dataAssetSync.ts: an upsert by assetPath that reloads the assets and bumps the CRC, so the hiss of both servers
// picks the rows up). Run from the repository root:
//
//   DATA_ASSET_TOKEN=<the server's DATA_ASSET_TOKEN> node dotnet/tools/assets/sync_assets.mjs <tsUrl> [manifest] [--apply]
//
// Without --apply it only lists what it would send. The manifest defaults to end-game-assets.json beside this file:
// every catalog row the End Game update needs (its battle pass, Fighter Passes, Chromium and other OVS skins, emotes,
// profile icons, taunts, announcer pack and badge, with the game's unreleased cosmetics in the pass), each
// { slug, assetType, assetPath, character_slug } as tested in game. Every row is sent enabled.
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const apply = args.includes("--apply");
const [baseUrl, manifestArg] = args.filter((a) => a !== "--apply");
if (!baseUrl) {
  console.error("usage: DATA_ASSET_TOKEN=... node dotnet/tools/assets/sync_assets.mjs <tsUrl> [manifest] [--apply]");
  process.exit(2);
}
const token = process.env.DATA_ASSET_TOKEN;
if (apply && !token) {
  console.error("DATA_ASSET_TOKEN is required with --apply");
  process.exit(2);
}

const manifest = manifestArg ?? path.join(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1")), "end-game-assets.json");
const rows = JSON.parse(fs.readFileSync(manifest, "utf8"));
const fields = ["slug", "assetType", "assetPath", "character_slug"];
for (const row of rows) {
  for (const field of fields) {
    if (typeof row[field] !== "string" || (field !== "character_slug" && row[field] === "")) {
      throw new Error(`${manifest}: ${JSON.stringify(row)} has no ${field}`);
    }
  }
}
const paths = new Set(rows.map((r) => r.assetPath));
if (paths.size !== rows.length) throw new Error(`${manifest}: an assetPath appears twice`);

console.log(`${rows.length} rows from ${manifest} -> ${baseUrl}/syncAsset${apply ? "" : " (dry run: add --apply to send)"}`);
let failed = 0;
for (const row of rows) {
  const body = { slug: row.slug, assetType: row.assetType, assetPath: row.assetPath, character_slug: row.character_slug, enabled: true };
  if (!apply) {
    console.log(`  ${row.assetType.padEnd(22)} ${row.slug}`);
    continue;
  }
  const res = await fetch(`${baseUrl.replace(/\/$/, "")}/syncAsset`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  if (res.status !== 200) failed++;
  console.log(`  ${res.status} ${row.slug}`);
}
if (apply) console.log(failed ? `${failed} of ${rows.length} failed` : `all ${rows.length} synced`);
process.exit(failed ? 1 : 0);
