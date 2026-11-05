// SSC functions the TS server answers with a fixed answer and no writes (game_install, game_launch_event,
// cancel_party_invite, decline_party_invite, update_party_game_modes, claim_mission_rewards, and
// get_or_create_mission_object, fixed but for the player's id), on the TS server and the C# port. Run from the repository root (it uses the TS server's node_modules):
//
//   node dotnet/tools/ssc/constants_diff.mjs run <baseUrl> <out.json>     # on scratch stores
//   node dotnet/tools/ssc/constants_diff.mjs diff <ts.json> <cs.json>
//
// run: REF_REDIS_URL, REF_MONGO_URI (scratch; wiped), REF_JWT_SECRET, REF_PROFILE=1. Each route gets a JSON body, a
// Hydra body, no body and a body that is not Hydra; every answer's status, content type and bytes are recorded, and
// both stores after it (nothing may be written). The mission object has no missions unless both servers run with them
// on (MISSIONS_ENABLED=true and Missions__Enabled=true): run once each way.
import { require, need, openScratch, readProfile, dump, writeRun, diff } from "../refdiff/refdiff.mjs";

const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");
const argv = process.argv;
process.argv = argv.slice(0, 2);
const { HydraEncoder } = await import(process.cwd() + "/node_modules/mvs-dump/dist/hydra/encoder.js");
process.argv = argv;

const ROUTES = [
  ["PUT", "game_install"],
  ["PUT", "game_launch_event"],
  ["PUT", "cancel_party_invite"],
  ["PUT", "decline_party_invite"],
  ["PUT", "update_party_game_modes"],
  ["POST", "claim_mission_rewards"],
  ["POST", "get_or_create_mission_object"],
];

function hydra(value) {
  const encoder = new HydraEncoder();
  encoder.encodeValue(value);
  return encoder.returnValue();
}

async function run(baseUrl, outFile) {
  const { redis, db, close } = await openScratch("constants_diff");
  const token = jwt.sign({ id: "0000000000000000000f0001" }, need("REF_JWT_SECRET"));
  const bodies = [
    ["json", "application/json", JSON.stringify({ LobbyId: "x", Modes: ["1v1"] })],
    ["hydra", "application/x-ag-binary", hydra({ LobbyId: "x", Modes: ["1v1"] })],
    ["empty-hydra", "application/x-ag-binary", Buffer.alloc(0)],
    ["bad-hydra", "application/x-ag-binary", Buffer.from([0xff, 0x01, 0x02])],
  ];
  const steps = [];
  for (const [method, route] of ROUTES) {
    for (const [kind, type, body] of bodies) {
      const response = await fetch(`${baseUrl}/ssc/invoke/${route}`, {
        method,
        headers: { "x-hydra-access-token": token, "x-real-ip": "198.51.100.9", "content-type": type },
        body,
        signal: AbortSignal.timeout(10000),
      }).catch((e) => ({ status: `<no answer: ${e.name}>`, headers: new Headers(), arrayBuffer: async () => new ArrayBuffer(0) }));
      const bytes = Buffer.from(await response.arrayBuffer());
      steps.push({
        name: `${route}-${kind}`,
        response: { status: response.status, type: response.headers.get("content-type"), bytes: bytes.toString("base64") },
        state: await dump(redis, db),
      });
    }
  }
  writeRun(outFile, baseUrl, Date.now(), steps, await readProfile(db, "constants_diff"));
  await close();
}

const [, , command, a, b] = process.argv;
if (command === "run" && a && b) await run(a, b);
else if (command === "diff" && a && b) diff(a, b, { writes: true });
else {
  console.error("usage: constants_diff.mjs run <baseUrl> <out.json> | diff <ts.json> <cs.json>");
  process.exit(2);
}
