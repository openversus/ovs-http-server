// Writes src/OpenVersus.Server.Core/Matches/stat-tracker-characters.json: the fighter (AssociatedCharacter) each stat
// tracker bundle counts for, from the TS server's data/inventoryDefs.ts, for the bundles whose slug the TS websocket's
// resolveStatTrackerValue reads a stat from (ending in highestdamagedealt, _highest_damage_dealt, totaldamagedealt,
// _total_damage_dealt, ringouts, wins, or wins and a number). A bundle with no fighter is left out (TS counted nothing for
// it either). Run from the repository root (it loads the TS source through @swc-node/register):
//
//   node dotnet/tools/matches/stat_tracker_characters.mjs
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
require(process.cwd() + "/node_modules/@swc-node/register");
const { INVENTORY_DEFINITIONS } = require(process.cwd() + "/src/data/inventoryDefs.ts");

const counted = (slug) => slug.endsWith("highestdamagedealt") || slug.endsWith("_highest_damage_dealt") || slug.endsWith("totaldamagedealt")
  || slug.endsWith("_total_damage_dealt") || slug.endsWith("ringouts") || slug.endsWith("wins") || /wins\d+$/.test(slug);
const out = Object.fromEntries(Object.entries(INVENTORY_DEFINITIONS)
  .filter(([slug, def]) => counted(slug) && typeof def?.data?.AssociatedCharacter === "string" && def.data.AssociatedCharacter.length > 0)
  .map(([slug, def]) => [slug, def.data.AssociatedCharacter])
  .sort(([a], [b]) => (a < b ? -1 : 1)));
const file = "dotnet/src/OpenVersus.Server.Core/Matches/stat-tracker-characters.json";
fs.writeFileSync(file, JSON.stringify(out, null, 1) + "\n");
console.log(`${Object.keys(out).length} bundles -> ${file}`);
