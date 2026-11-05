import assert from "node:assert/strict";
import test from "node:test";
import { CHROMIUM_BATCH5, CHROMIUM_BATCH5_ASSETS, CHROMIUM_BATCH5_INVENTORY } from "../src/data/chromiumSkins";
import { INVENTORY_DEFINITIONS, InventoryDefData } from "../src/data/inventoryDefs";
import { ENABLED_SKINS } from "../src/data/skins";
const defaults:Record<string,string>={character_velma:"skin_velma_default",character_steven:"skin_steven_default",character_jake:"skin_jake_default",character_garnet:"skin_garnet_default",character_creature:"skin_creature_default"};
test("Chromium batch five catalog, inventory and native portraits agree",()=>{
 assert.equal(CHROMIUM_BATCH5.length,5);const ids=new Set<string>();
 for(const [i,skin] of CHROMIUM_BATCH5.entries()){
  const asset=CHROMIUM_BATCH5_ASSETS[i],item=CHROMIUM_BATCH5_INVENTORY[skin.slug],native=INVENTORY_DEFINITIONS[defaults[skin.characterSlug]].data as InventoryDefData,data=item.data as InventoryDefData;
  assert.equal(asset.assetPath,`/OVS/Rewards/Skins/Chromium/${skin.folder}/Catalog/${skin.slug}.${skin.slug}`);
  assert.equal(asset.character_slug,skin.characterSlug);assert.equal(INVENTORY_DEFINITIONS[skin.slug],item);
  assert.equal(data.AssetPath,asset.assetPath);assert.equal(data.AssociatedCharacter,native.AssociatedCharacter);
  assert.equal(data.RewardThumbnail,native.RewardThumbnail);assert.equal(data.RewardThumbnailMaterial,native.RewardThumbnailMaterial);
  assert.ok(!item.tags.includes("unlock_location_battlepass"));assert.ok(!ids.has(item.id));ids.add(item.id);
 }
});
test("each batch-five skin has one exact owner and keeps default first",()=>{
 for(const skin of CHROMIUM_BATCH5){const owners=Object.entries(ENABLED_SKINS).filter(([,v])=>(v.Slugs as readonly string[]).includes(skin.slug)).map(([k])=>k);assert.deepEqual(owners,[skin.characterSlug]);const list=ENABLED_SKINS[skin.characterSlug as keyof typeof ENABLED_SKINS].Slugs;assert.equal(list[0],defaults[skin.characterSlug]);assert.equal(list.filter(v=>v===skin.slug).length,1);}
});
test("batch-five IDs and slugs are globally unique",()=>{for(const skin of CHROMIUM_BATCH5){const item=CHROMIUM_BATCH5_INVENTORY[skin.slug];assert.equal(Object.values(INVENTORY_DEFINITIONS).filter(v=>v.id===item.id).length,1);assert.equal(Object.keys(INVENTORY_DEFINITIONS).filter(s=>s===skin.slug).length,1);}});
