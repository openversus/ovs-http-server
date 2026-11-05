// Writes src/OpenVersus.Server.Core/Matchmaking/bot-fighters.json: the fighters a Casual bot is picked from, each with the
// skins it may wear. The fighters are the TS server's ENABLED_SKINS (src/data/skins.ts: the characters players can use,
// with the skins they can equip); a skin is kept only when its inventory definition exists and is not marked
// EnabledForShipping false (src/data/inventoryDefs.ts), so a bot never wears one the game may not load. A character's own
// EnabledForShipping is not read: it is false for some fighters people play (Jason, Agent Smith). Skins the game itself
// does not have are left out too (NOT_IN_GAME): a fighter in one spawns with no skin applied, invisible, and crashes both
// players when a move re-applies it (Arya's face steal on skin_c006_s15, a Random fighter, 2026-10-24).
// Run from the repository root: node -r @swc-node/register dotnet/tools/casual/gen_bot_fighters.mjs
import fs from "node:fs";
import { createRequire } from "node:module";

// Enabled skins whose SkinData is in none of the game's paks (pakchunk0-Windows manifest, checked 2026-10-24): WB listed
// them but never shipped them, and the client hides them from the variant list.
const NOT_IN_GAME = new Set([
  "skin_c001_s15", // Wonder Woman
  "skin_c006_s15", // Arya
  "skin_c010_s15", // Tom & Jerry
  "skin_c011_s03", "skin_c011_s06", "skin_c011_s09", "skin_c011_s15", // Steven (Skins/_Unreleased)
  "skin_c014_s15", // Velma
]);

const require = createRequire(import.meta.url);
const { ENABLED_SKINS } = require(process.cwd() + "/src/data/skins.ts");
const { INVENTORY_DEFINITIONS } = require(process.cwd() + "/src/data/inventoryDefs.ts");
const fighters = {};
let dropped = 0;
for (const [character, { Slugs }] of Object.entries(ENABLED_SKINS)) {
  const skins = [...new Set(Slugs)].filter((slug) => INVENTORY_DEFINITIONS[slug] && INVENTORY_DEFINITIONS[slug].data?.EnabledForShipping !== false
    && !NOT_IN_GAME.has(slug));
  dropped += new Set(Slugs).size - skins.length;
  if (skins.length > 0) fighters[character] = skins;
}
const out = "dotnet/src/OpenVersus.Server.Core/Matchmaking/bot-fighters.json";
fs.writeFileSync(out, JSON.stringify(fighters, null, 1) + "\n");
console.log(`${Object.keys(fighters).length} fighters, ${Object.values(fighters).reduce((n, s) => n + s.length, 0)} skins (${dropped} left out) -> ${out}`);
