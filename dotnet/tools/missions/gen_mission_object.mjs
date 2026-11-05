// Writes src/OpenVersus.Server.Core/Missions/mission-object.json: the TS server's get_or_create_mission_object answer
// (handlers/ssc.ts, handleSsc_invoke_get_or_create_mission_object), read out of its source with the TypeScript compiler,
// with owner_id "" (the C# port puts the player's id there). Run from the repository root:
//
//   node dotnet/tools/missions/gen_mission_object.mjs
//
// The literal is one WB account's mission object (its id is the one in WB's own websocket messages, created 2024-05-30):
// its progress, GUIDs and dates are that account's, not anyone's state. See docs/MISSIONS.md.
import fs from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(process.cwd() + "/");
const ts = require("typescript");
const source = "src/handlers/ssc.ts";
const out = "dotnet/src/OpenVersus.Server.Core/Missions/mission-object.json";

const file = ts.createSourceFile(source, fs.readFileSync(source, "utf8"), ts.ScriptTarget.Latest, true);
let literal;
const visit = (node) => {
  if (ts.isFunctionDeclaration(node) && node.name?.text === "handleSsc_invoke_get_or_create_mission_object") {
    const find = (n) => {
      if (ts.isVariableDeclaration(n) && n.name.getText() === "missionObject") literal = n.initializer;
      else ts.forEachChild(n, find);
    };
    find(node);
  }
  ts.forEachChild(node, visit);
};
visit(file);
if (!literal || !ts.isObjectLiteralExpression(literal)) {
  console.error(`no missionObject literal in ${source}`);
  process.exit(1);
}

// The literal's only free name is aID (the player's id).
const js = ts.transpile(`(aID) => (${literal.getText()})`, { target: ts.ScriptTarget.ES2020 });
const value = (0, eval)(js)("");
if (value?.body?.owner_id !== "") {
  console.error("owner_id is not the player's id: the literal changed shape");
  process.exit(1);
}
fs.writeFileSync(out, JSON.stringify(value, null, 2) + "\n");
console.log(`${out}: ${Object.keys(value.body.server_data.MissionControllerContainers).length} containers`);
