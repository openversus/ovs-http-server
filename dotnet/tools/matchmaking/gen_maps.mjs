// Writes src/OpenVersus.Server.Core/Matchmaking/maps.json: the maps the TS matchmaker picks from (src/data/maps1v1.json
// and maps2v2.json, each map with its enabled flag; the TS worker reads them for every match) and the lists it falls
// back on when none is enabled (maps1v1 / maps2v2 in src/data/maps.ts). Until the TS worker is gone, a map change goes
// to the TS files and this is run again; --check says whether the two still agree.
// Run from the repository root: node dotnet/tools/matchmaking/gen_maps.mjs [--check]
import fs from "node:fs";

// The fallback arrays, read from the source (importing data/maps.ts needs the server's environment); commented-out
// entries are not in them.
const source = fs.readFileSync("src/data/maps.ts", "utf8");
const array = (name) => [...new RegExp(`export const ${name} = \\[([\\s\\S]*?)\\];`).exec(source)[1].replace(/\/\/.*$/gm, "").matchAll(/"([^"]+)"/g)].map((m) => m[1]);
const maps1v1 = array("maps1v1"), maps2v2 = array("maps2v2");
const list = (file) => JSON.parse(fs.readFileSync(file, "utf8")).map((o) => Object.values(o)[0]).map((m) => ({ id: m.id, enabled: m.enabled === true }));
const maps = {
  "1v1": list("src/data/maps1v1.json"),
  "2v2": list("src/data/maps2v2.json"),
  fallback: { "1v1": maps1v1, "2v2": maps2v2 },
};
const out = "dotnet/src/OpenVersus.Server.Core/Matchmaking/maps.json";
const text = JSON.stringify(maps, null, 1) + "\n";
if (process.argv.includes("--check")) {
  const same = fs.existsSync(out) && fs.readFileSync(out, "utf8") === text;
  console.log(same ? "maps.json matches the TS map lists" : "maps.json differs from the TS map lists: run without --check");
  process.exit(same ? 0 : 1);
}
fs.writeFileSync(out, text);
console.log(`${maps["1v1"].length} 1v1 and ${maps["2v2"].length} 2v2 maps (${maps["1v1"].filter((m) => m.enabled).length}/${maps["2v2"].filter((m) => m.enabled).length} enabled) -> ${out}`);
