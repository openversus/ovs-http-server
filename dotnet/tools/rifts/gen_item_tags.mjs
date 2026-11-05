// Writes src/OpenVersus.Server.Core/Rifts/rift-item-tags.json from the TS server's INVENTORY_DEFINITIONS
// (src/data/inventoryDefs.ts): each skin's and character's gameplay tags (data.ItemRewardTags.GameplayTags), which rift
// missions test ("Objective:Match:Tag:Skin" == a tag). Neither the data assets in Mongo nor the hiss hold them for every
// skin. Run from the repository root:
//   node dotnet/tools/rifts/gen_item_tags.mjs
// Rerun when the inventory definitions change.
import fs from "node:fs";
import path from "node:path";
import { stripTypeScriptTypes } from "node:module";

const root = process.cwd();
const source = fs.readFileSync(path.join(root, "src/data/inventoryDefs.ts"), "utf8");
const start = source.indexOf("export const INVENTORY_DEFINITIONS");
if (start < 0) throw new Error("INVENTORY_DEFINITIONS was not found in src/data/inventoryDefs.ts; update this script");
const from = source.indexOf("{", source.indexOf("=", start));
const text = source.slice(from, source.lastIndexOf("}") + 1);
const definitions = new Function(`return ${stripTypeScriptTypes(`(${text})`)}`)();

const tags = {};
for (const [slug, item] of Object.entries(definitions).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
  if (!slug.startsWith("skin_") && !slug.startsWith("character_")) continue;
  const list = item?.data?.ItemRewardTags?.GameplayTags;
  if (Array.isArray(list) && list.length > 0) tags[slug] = list;
}

const out = path.join(root, "dotnet/src/OpenVersus.Server.Core/Rifts/rift-item-tags.json");
fs.writeFileSync(out, JSON.stringify(tags, null, 1) + "\n");
console.log(`${Object.keys(tags).length} skins and characters -> ${path.relative(root, out)}`);
