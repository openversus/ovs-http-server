// Writes src/OpenVersus.Server.Core/CustomLobbies/game-modes.json: what the TS custom lobby reads from each game mode
// (GAME_MODES_CONFIG in src/modules/customLobby/gameModes.data.ts) and its map rotation (MAP_ROTATIONS in maps.data.ts),
// as the TS server reads them: getCustomLobbyDefaultSettings (TeamStyle, the first team's NumRingouts, MatchDuration,
// bMapHazards, RequiredWorldBuffs), getGameModeMaps (the rotation's maps; absent when the rotation or its maps are) and
// computeBuffMatrix (each team's RequiredTeamPlayerBuffs and each player slot's RequiredPlayerBuffs). A field the TS data
// lacks is left out here too: the TS server writes nothing for it (JSON.stringify drops undefined).
// Run from the repository root: node -r @swc-node/register dotnet/tools/customlobby/gen_game_modes.mjs
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { GAME_MODES_CONFIG } = require(process.cwd() + "/src/modules/customLobby/gameModes.data.ts");
const { MAP_ROTATIONS } = require(process.cwd() + "/src/modules/customLobby/maps.data.ts");

const modes = {};
for (const [slug, mode] of Object.entries(GAME_MODES_CONFIG)) {
  const gm = mode.data.GameModeData;
  const rotation = MAP_ROTATIONS[gm.MapRotation]?.data?.MapsInRotation?.map((m) => m.Map);
  modes[slug] = {
    TeamStyle: gm.TeamStyle,
    NumRingouts: gm.GameModeTeams[0]?.NumRingouts,
    MatchDuration: gm.MatchDuration,
    bMapHazards: gm.bMapHazards,
    MapRotation: gm.MapRotation,
    Maps: rotation,
    RequiredWorldBuffs: gm.RequiredWorldBuffs,
    Teams: gm.GameModeTeams.map((t) => ({
      RequiredTeamPlayerBuffs: t.RequiredTeamPlayerBuffs,
      Players: (t.Players ?? []).map((p) => p.RequiredPlayerBuffs ?? null),
    })),
  };
}
const out = "dotnet/src/OpenVersus.Server.Core/CustomLobbies/game-modes.json";
fs.writeFileSync(out, JSON.stringify(modes, null, 1) + "\n");
const noMaps = Object.entries(modes).filter(([, m]) => !m.Maps).map(([s]) => s);
const noTeams = Object.entries(modes).filter(([, m]) => !m.Teams.length).map(([s]) => s);
console.log(`${Object.keys(modes).length} modes -> ${out}; without maps: ${noMaps.join(", ") || "none"}; without teams: ${noTeams.join(", ") || "none"}`);
