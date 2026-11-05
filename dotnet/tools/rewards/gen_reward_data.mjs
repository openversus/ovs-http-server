// Writes src/OpenVersus.Server.Core/RewardTracks/reward-data.json from an FModel JSON export of the game's assets
// ("Save Properties (.json)"): what the servers need to pay rewards, which the hiss does not carry. Run from the
// repository root:
//
//   node dotnet/tools/rewards/gen_reward_data.mjs <export root>      # the folder holding MultiVersus/
//
//   rewards           every MvsRewardHsda by its Slug (the RewardHsda a RewardTableLookup names; the Slug, not the asset
//                     name: reward_perk_currency_80's is "reard_perk_currency_80"): what ItemReward points at, as
//                     {type, target, count}: currency (target: the MvsCurrencyHsda's Slug, an inventory item such as
//                     perk_currency or match_toasts), xp (target: the MvsXpRewardHsda's TagToApplyXp in the hiss's
//                     colon form, which tracks with that XpRewardGrantTag take), gem, lootbox, reward (another reward),
//                     item (a cosmetic, character, ... : its Slug), unresolved (ItemReward not in the export)
//   xpSources         every MvsMatchXpSourceHsda by Slug (lower case): FModel leaves out values equal to the class
//                     default, so absent fields are written as the defaults the exports imply: XpSourceType
//                     InstancedModifier (XPSRC_Base names BaseConfig explicitly), modifiers 1.0 (only 5.0, 2.5 and 0.0
//                     are ever written), BaseConfig from XPSRC_Base
//   characterTracks   each CharacterData's Slug -> its MrtSlug (its mastery track)
import fs from "node:fs";
import path from "node:path";

const [, , exportRoot] = process.argv;
if (!exportRoot) {
  console.error("usage: gen_reward_data.mjs <export root>");
  process.exit(2);
}
const root = path.join(exportRoot, "MultiVersus");
const out = "dotnet/src/OpenVersus.Server.Core/RewardTracks/reward-data.json";

function* walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(p);
    else if (entry.name.endsWith(".json")) yield p;
  }
}

// An asset reference (/Game/X/Y.Y, /<Plugin>/X/Y.Y) to its exported file.
function file(assetPath) {
  const p = assetPath.split(".")[0];
  if (p.startsWith("/Game/")) return path.join(root, "Content", p.slice(6) + ".json");
  const [plugin, ...rest] = p.replace(/^\//, "").split("/");
  return path.join(root, "Plugins", "GameFeatures", plugin, "Content", rest.join("/") + ".json");
}

function read(p) {
  try {
    const j = JSON.parse(fs.readFileSync(p, "utf8"));
    return Array.isArray(j) ? j : [];
  } catch {
    return [];
  }
}

// Only small files whose start names a type read here (the export is ~15 GB, mostly meshes and textures).
const wanted = ["MvsRewardHsda", "MvsMatchXpSourceHsda", "CharacterData"];
const rewards = {}, xpSources = {}, characterTracks = {};
let scanned = 0;
for (const p of walk(root)) {
  if (fs.statSync(p).size > 2_000_000) continue;
  const fd = fs.openSync(p, "r");
  const head = Buffer.alloc(300);
  fs.readSync(fd, head, 0, 300, 0);
  fs.closeSync(fd);
  const text = head.toString();
  if (!wanted.some((t) => text.includes(`"${t}"`))) continue;
  scanned++;
  for (const o of read(p)) {
    const props = o.Properties ?? {};
    if (o.Type === "MvsRewardHsda" && props.Slug) {
      const target = props.ItemReward?.AssetPathName ?? "";
      const asset = target ? read(file(target))[0] : undefined;
      const a = asset?.Properties ?? {};
      const count = props.Count ?? 1;
      let entry;
      switch (asset?.Type) {
        case undefined: entry = { type: "unresolved", target, count }; break;
        case "MvsCurrencyHsda": entry = { type: "currency", target: a.Slug, count }; break;
        case "MvsXpRewardHsda": entry = { type: "xp", target: (a.TagToApplyXp?.TagName ?? "").replaceAll(".", ":"), count }; break;
        case "MvsGemHsda": entry = { type: "gem", target: a.Slug ?? asset.Name, count }; break;
        case "MvsLootBoxHsda": entry = { type: "lootbox", target: a.Slug ?? asset.Name, count }; break;
        case "MvsRewardHsda": entry = { type: "reward", target: a.Slug ?? asset.Name, count }; break;
        default: entry = { type: "item", target: a.Slug ?? asset.Name, count };
      }
      rewards[props.Slug] = entry;
    } else if (o.Type === "MvsMatchXpSourceHsda" && props.Slug) {
      xpSources[props.Slug.replace(/\s+/g, "").toLowerCase()] = {
        type: (props.XpSourceType ?? "EMvsMatchXpSourceType::InstancedModifier").split("::")[1],
        base: props.BaseConfig ?? null,
        winModifier: props.InstancedModifier?.BaseWinXpModifier ?? 1.0,
        lossModifier: props.InstancedModifier?.BaseLossXpModifier ?? 1.0,
        drawModifier: props.InstancedModifier?.BaseDrawXpModifier ?? 1.0,
      };
    } else if (o.Type === "CharacterData" && props.Slug && props.MrtSlug) {
      characterTracks[props.Slug] = props.MrtSlug;
    }
  }
}

const sorted = (o) => Object.fromEntries(Object.entries(o).sort(([a], [b]) => a.localeCompare(b)));
fs.writeFileSync(out, JSON.stringify({ rewards: sorted(rewards), xpSources: sorted(xpSources), characterTracks: sorted(characterTracks) }, null, 1) + "\n");
const types = {};
for (const r of Object.values(rewards)) types[r.type] = (types[r.type] ?? 0) + 1;
console.log(`${out}: ${scanned} assets read; rewards ${Object.keys(rewards).length} ${JSON.stringify(types)}, xpSources ${Object.keys(xpSources).length}, characterTracks ${Object.keys(characterTracks).length}`);
