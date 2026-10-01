// Writes src/OpenVersus.Server.Core/CustomLobbies/Scripts/*.lua: the TS server's custom lobby Lua scripts (the LUA_*
// constants of src/modules/customLobby/lobby.service.ts), byte for byte, with ${LOBBY_EX} filled in. The C# custom lobby
// starts from these and changes them only where a TS bug is fixed (each change asserted by
// tools/matches/custom_lobby_diff.mjs); --check lists the scripts that differ from the TS source, so those changes stay
// visible. Three scripts are left out: nothing in the TS server calls them (setLobbyJoinable, setLobbyMode, lockLobby).
// Run from the repository root: node dotnet/tools/customlobby/gen_scripts.mjs [--check]
import fs from "node:fs";

const SOURCE = "src/modules/customLobby/lobby.service.ts";
const OUT = "dotnet/src/OpenVersus.Server.Core/CustomLobbies/Scripts";
const DEAD = new Set(["LUA_SET_LOBBY_JOINABLE", "LUA_SET_LOBBY_MODE", "LUA_LOCK_LOBBY"]);

const source = fs.readFileSync(SOURCE, "utf8");
const lobbyEx = /const LOBBY_EX = ([^;]+);/.exec(source)[1];
const seconds = Function(`return (${lobbyEx.replace(/\/\/.*$/, "")})`)();
const scripts = {};
for (const m of source.matchAll(/const (LUA_\w+) = `([\s\S]*?)`;/g)) {
  if (DEAD.has(m[1])) continue;
  const unknown = [...m[2].matchAll(/\$\{(\w+)\}/g)].map((x) => x[1]).filter((n) => n !== "LOBBY_EX");
  if (unknown.length) throw new Error(`${m[1]}: unknown placeholder ${unknown.join(", ")}`);
  scripts[m[1]] = m[2].replaceAll("${LOBBY_EX}", String(seconds));
}

const check = process.argv.includes("--check");
let differ = 0;
for (const [name, text] of Object.entries(scripts)) {
  const file = `${OUT}/${name.slice(4).toLowerCase()}.lua`;
  if (check) {
    const now = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (now !== text) { differ++; console.log(`${now === null ? "missing" : "changed"}: ${file}`); }
  } else {
    fs.writeFileSync(file, text);
  }
}
console.log(check ? `${differ} of ${Object.keys(scripts).length} differ from ${SOURCE}` : `${Object.keys(scripts).length} scripts (LOBBY_EX ${seconds}) -> ${OUT}`);
