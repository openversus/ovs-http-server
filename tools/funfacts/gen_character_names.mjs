// Writes src/OpenVersus.Server.Core/FunFacts/character-names.json: the DisplayName the TS server's fun facts show for
// each inventory definition that has one (prettyChar in legacy/reference/ts_server/src/services/funFactsService.ts reads
// INVENTORY_DEFINITIONS[slug].data.DisplayName). Only character_* slugs are kept: prettyChar is only given those.
// Run from the repository root: node -r ./legacy/reference/ts_server/node_modules/@swc-node/register tools/funfacts/gen_character_names.mjs
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { INVENTORY_DEFINITIONS } = require(process.cwd() + "/legacy/reference/ts_server/src/data/inventoryDefs.ts");
const names = {};
for (const [slug, def] of Object.entries(INVENTORY_DEFINITIONS)) {
  const name = def?.data?.DisplayName;
  if (slug.startsWith("character_") && typeof name === "string" && name.length > 0) names[slug] = name;
}
const out = "src/OpenVersus.Server.Core/FunFacts/character-names.json";
fs.writeFileSync(out, JSON.stringify(names, null, 1) + "\n");
console.log(`${Object.keys(names).length} names -> ${out}`);
