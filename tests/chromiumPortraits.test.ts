import assert from "node:assert/strict";
import test from "node:test";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";

for (const folder of ["Shaggy", "BananaGuard", "Superman"]) {
  test(`Chromium ${folder} uses its own packaged portrait`, () => {
    const slug = `skin_ovs_chromium_${folder.toLowerCase()}`;
    const data = INVENTORY_DEFINITIONS[slug].data as InventoryDefData;
    const root = `/OVS/Rewards/Skins/Chromium/${folder}/UI/`;
    assert.equal(data.RewardThumbnail, `${root}T_Chromium_${folder}_Portrait.T_Chromium_${folder}_Portrait`);
    assert.equal(data.RewardThumbnailMaterial, `${root}MI_Chromium_${folder}_Portrait.MI_Chromium_${folder}_Portrait`);
    assert.ok(!data.RewardThumbnail.startsWith("/Game/"));
  });
}
