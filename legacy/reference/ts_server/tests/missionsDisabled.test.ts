import assert from "node:assert/strict";
import test from "node:test";
import jwt from "jsonwebtoken";
import { HYDRA_ACCESS_TOKEN, SECRET } from "../src/middleware/auth";
import { applyMissionsSwitch, handleSsc_invoke_get_or_create_mission_object } from "../src/handlers/ssc";

async function missionObject() {
  const token = jwt.sign({ id: "account-a" }, SECRET);
  let sent: any;
  await handleSsc_invoke_get_or_create_mission_object(
    { headers: { [HYDRA_ACCESS_TOKEN]: token }, token: { id: "account-a" } } as any,
    { send: (body: any) => { sent = body; } } as any,
  );
  return sent.body;
}

test("with MISSIONS_ENABLED off (the default) players get no missions at all", async () => {
  const body = await missionObject();
  assert.deepEqual(body.server_data.MissionControllerContainers, {});
  assert.equal(body.owner_id, "account-a");
  assert.equal(body.object_type_slug, "player-missions");
});

test("switching MISSIONS_ENABLED on keeps the full mission set", () => {
  const full = { body: { server_data: { MissionControllerContainers: { miscon_ftue: {} } } } };
  assert.deepEqual(applyMissionsSwitch(structuredClone(full), true), full);
  assert.deepEqual(applyMissionsSwitch(structuredClone(full), false).body.server_data.MissionControllerContainers, {});
});
