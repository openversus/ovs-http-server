// Writes src/OpenVersus.Server.Core/Seasons/seasonal-data.json: the login profile's SeasonalData entries for seasons the
// TS server's literal (login-response.json) does not have, from a live export of an account's profile made while the
// service still ran. The login response adds the entry for the current season (Season:Current) when the literal has
// none. Run from the repository root:
//
//   node dotnet/tools/seasons/gen_seasonal_data.mjs <profile.json> [season ...]
//
// <profile.json>: an exported profile (server_data.SeasonalData); the seasons default to Season:SeasonSix. The export
// writes dates as ISO text; the game's answers carry them as Hydra dates, written here as {"_hydra_unix_date": seconds}
// as the TS literal writes them.
import fs from "node:fs";

const [, , file, ...wanted] = process.argv;
if (!file) {
  console.error("usage: gen_seasonal_data.mjs <profile.json> [season ...]");
  process.exit(2);
}
const seasons = wanted.length ? wanted : ["Season:SeasonSix"];
const seasonal = JSON.parse(fs.readFileSync(file, "utf8"))?.server_data?.SeasonalData ?? {};
const iso = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)$/;
let dates = 0;
const convert = (v) => {
  if (typeof v === "string" && iso.test(v)) {
    dates++;
    return { _hydra_unix_date: Math.floor(Date.parse(v) / 1000) };
  }
  if (Array.isArray(v)) return v.map(convert);
  if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, convert(x)]));
  return v;
};
const out = {};
for (const season of seasons) {
  if (!seasonal[season]) throw new Error(`${season} is not in ${file}`);
  out[season] = convert(seasonal[season]);
}
const target = "dotnet/src/OpenVersus.Server.Core/Seasons/seasonal-data.json";
fs.writeFileSync(target, JSON.stringify(out, null, 2) + "\n");
console.log(`${seasons.join(", ")} -> ${target} (${dates} dates converted)`);
