// Writes src/OpenVersus.Server.Core/Matchmaking/bot-fighters.json: the fighters a Casual bot is picked from, each with the
// skins it may wear. The fighters are the TS server's ENABLED_SKINS (src/data/skins.ts: the characters players can use,
// with the skins they can equip); a skin is kept only when its inventory definition exists and is not marked
// EnabledForShipping false (src/data/inventoryDefs.ts), so a bot never wears one the game may not load. A character's own
// EnabledForShipping is not read: it is false for some fighters people play (Jason, Agent Smith).
// Run from the repository root: node -r @swc-node/register dotnet/tools/casual/gen_bot_fighters.mjs
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { ENABLED_SKINS } = require(process.cwd() + "/src/data/skins.ts");
const { INVENTORY_DEFINITIONS } = require(process.cwd() + "/src/data/inventoryDefs.ts");
const fighters = {};
let dropped = 0;
for (const [character, { Slugs }] of Object.entries(ENABLED_SKINS)) {
  const skins = [...new Set(Slugs)].filter((slug) => INVENTORY_DEFINITIONS[slug] && INVENTORY_DEFINITIONS[slug].data?.EnabledForShipping !== false);
  dropped += new Set(Slugs).size - skins.length;
  if (skins.length > 0) fighters[character] = skins;
}
const out = "dotnet/src/OpenVersus.Server.Core/Matchmaking/bot-fighters.json";
fs.writeFileSync(out, JSON.stringify(fighters, null, 1) + "\n");
console.log(`${Object.keys(fighters).length} fighters, ${Object.values(fighters).reduce((n, s) => n + s.length, 0)} skins (${dropped} left out) -> ${out}`);
