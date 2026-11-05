// Writes src/OpenVersus.Server.Core/Inventory/inventory-restricted.json from the TS server's data: the items no
// player owns until they are paid them (Ownership.cs), End Game's battle pass rewards (milestones.ts
// OVS_BATTLEPASS_REWARD_SLUGS, owned once their tier is claimed) and Chromium skins (chromiumSkins.ts, the Fighter
// Passes' last tier), and the OVS Dev badge (OVS_DEV_ACCOUNT_IDS only). Run from the repository root:
//
//   node --env-file=legacy/reference/ts_server/.env --require ./legacy/reference/ts_server/node_modules/@swc-node/register tools/inventory/gen_restricted.ts   (the TS env, as the server needs)
import fs from "node:fs";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "../../legacy/reference/ts_server/src/data/milestones";
import { CHROMIUM_SKIN_SLUGS } from "../../legacy/reference/ts_server/src/data/chromiumSkins";
import { OVS_DEV_BADGE_SLUG } from "../../legacy/reference/ts_server/src/data/ovsDevBadge";

const out = "src/OpenVersus.Server.Core/Inventory/inventory-restricted.json";
fs.writeFileSync(out, JSON.stringify({
  battlePass: [...OVS_BATTLEPASS_REWARD_SLUGS],
  fighterPass: [...CHROMIUM_SKIN_SLUGS],
  devBadge: OVS_DEV_BADGE_SLUG,
}, null, 2) + "\n");
console.log(`wrote ${out}`);
