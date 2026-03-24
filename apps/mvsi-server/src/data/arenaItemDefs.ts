import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

// ─── Types ────────────────────────────────────────────────────────────────────

type TagWeightScalar = {
  classIndex: number;
  scalar: number;
  absolute: number;
};

export type ArenaItemDef = {
  slug: string;
  gemName: string;
  rarity: number; // 0=Common, 1=Uncommon, 2=Rare, 3=Epic, 4=Legendary, 5=Mythic
  cost: number;
  startingWeight: number;
  weightPerRound: number;
  tagWeightScalars: TagWeightScalar[];
};

export type ShopItem = {
  Item: {
    Slug: string;
    SellValue: number;
    Level: number;
    Xp: number;
    NextLevelXp: number;
  };
  Cost: number;
};

export type ShopOption = {
  CurrentShop: ShopItem[];
};

// ─── Maps ─────────────────────────────────────────────────────────────────────

const RARITY_MAP: Record<string, number> = {
  "EArenaItemRarity::Common": 0,
  "EArenaItemRarity::Uncommon": 1,
  "EArenaItemRarity::Rare": 2,
  "EArenaItemRarity::Epic": 3,
  "EArenaItemRarity::Legendary": 4,
  "EArenaItemRarity::Mythic": 5,
};

const CLASS_TAG_MAP: Record<string, number> = {
  "TS.Fixed.Class.Support": 0,
  "TS.Fixed.Class.Assassin": 1,
  "TS.Fixed.Class.Bruiser": 2,
  "TS.Fixed.Class.Tank": 3,
  "TS.Fixed.Class.Mage": 4,
};

// ─── Gem name extraction ─────────────────────────────────────────────────────

export function getGemName(slug: string): string {
  // slug: "arenaitem_attackpower" → gemName comes from the Gem.AssetPathName
  // We store gemName directly in ArenaItemDef, use this as a lookup helper
  const def = ALL_ARENA_ITEMS.find((d) => d.slug === slug);
  return def?.gemName ?? "";
}

// ─── Load arena items ─────────────────────────────────────────────────────────

function loadArenaItems(): ArenaItemDef[] {
  const dir = join(import.meta.dir, "arenaItems");
  const files = readdirSync(dir).filter((f) => f.endsWith(".json"));
  const items: ArenaItemDef[] = [];

  for (const file of files) {
    try {
      const raw = readFileSync(join(dir, file), "utf-8");
      const parsed = JSON.parse(raw) as Array<{ Properties: Record<string, any> }>;
      const props = parsed[0]?.Properties;
      if (!props || !props.Slug) continue;

      const rarityStr: string = props.Rarity ?? "EArenaItemRarity::Common";
      const rarity = RARITY_MAP[rarityStr] ?? 0;
      const cost = 4 + rarity * 2;

      // Extract gem name from AssetPathName:
      // e.g. ".../Gem_Arena_AttackPower.Gem_Arena_AttackPower" → "Gem_arena_attackpower"
      const assetPath: string = props.Gem?.AssetPathName ?? "";
      const gemSuffix = assetPath.split("/").pop()?.split(".")[0] ?? "";
      const gemName = gemSuffix.toLowerCase();

      const tagWeightScalars: TagWeightScalar[] = (props.TagWeightScalars ?? [])
        .map((entry: any) => {
          const classIndex = CLASS_TAG_MAP[entry.Key?.TagName];
          if (classIndex === undefined) return null;
          return {
            classIndex,
            scalar: entry.Value?.Scalar ?? 1,
            absolute: entry.Value?.Absolute ?? 0,
          };
        })
        .filter(Boolean) as TagWeightScalar[];

      items.push({
        slug: props.Slug as string,
        gemName,
        rarity,
        cost,
        startingWeight: props.StartingWeight ?? 0,
        weightPerRound: props.WeightPerRound ?? 0,
        tagWeightScalars,
      });
    } catch {
      // skip malformed files
    }
  }

  return items;
}

export const ALL_ARENA_ITEMS: ArenaItemDef[] = loadArenaItems();

// ─── Rarity helpers ──────────────────────────────────────────────────────────

const RARITY_NAME_TO_NUM: Record<string, number> = {
  Common: 0,
  Uncommon: 1,
  Rare: 2,
  Epic: 3,
  Legendary: 4,
  Mythic: 5,
};

// Group items by rarity at load time for fast lookup
const ITEMS_BY_RARITY: Record<number, ArenaItemDef[]> = {};
for (const item of ALL_ARENA_ITEMS) {
  if (!ITEMS_BY_RARITY[item.rarity]) ITEMS_BY_RARITY[item.rarity] = [];
  ITEMS_BY_RARITY[item.rarity].push(item);
}

// ─── Item pool generation ────────────────────────────────────────────────────

/**
 * Generate the shared item pool for an arena.
 * For each rarity, randomly picks N items (with replacement) from the
 * available items of that rarity. Returns slug → count.
 */
export function generateItemPool(
  itemAmounts: Record<string, number>,
): Record<string, number> {
  const pool: Record<string, number> = {};

  for (const [rarityName, totalCount] of Object.entries(itemAmounts)) {
    const rarityNum = RARITY_NAME_TO_NUM[rarityName];
    if (rarityNum === undefined) continue;
    const items = ITEMS_BY_RARITY[rarityNum];
    if (!items || items.length === 0) continue;

    for (let i = 0; i < totalCount; i++) {
      const item = items[Math.floor(Math.random() * items.length)];
      pool[item.slug] = (pool[item.slug] ?? 0) + 1;
    }
  }

  return pool;
}

// ─── Shop generation ──────────────────────────────────────────────────────────

/**
 * Generate `count` shop options, each containing `itemsPerOption` items.
 * Uses the shared item pool and ShopLevelWeights to determine rarity
 * distribution per round. Items shown in shop are NOT removed from pool —
 * only actual purchases remove items.
 */
export function generateShopOptions(
  pool: Record<string, number>,
  shopLevelWeights: { RarityWeight: Record<string, number> }[],
  round: number,
  count = 6,
  itemsPerOption = 4,
): ShopOption[] {
  const weightIdx = Math.min(round - 1, shopLevelWeights.length - 1);
  const rarityWeights = shopLevelWeights[weightIdx].RarityWeight;

  // Build weighted rarity entries (only rarities with weight > 0)
  const rarities: { num: number; weight: number }[] = [];
  for (const [name, weight] of Object.entries(rarityWeights)) {
    if (weight > 0) {
      rarities.push({ num: RARITY_NAME_TO_NUM[name] ?? 0, weight });
    }
  }

  function rollRarity(): number {
    const total = rarities.reduce((a, r) => a + r.weight, 0);
    let r = Math.random() * total;
    for (const rar of rarities) {
      r -= rar.weight;
      if (r <= 0) return rar.num;
    }
    return rarities[rarities.length - 1].num;
  }

  const options: ShopOption[] = [];
  for (let i = 0; i < count; i++) {
    const shopItems: ShopItem[] = [];
    for (let j = 0; j < itemsPerOption; j++) {
      const rarity = rollRarity();
      // Prefer items that still have copies in the pool
      const items = ITEMS_BY_RARITY[rarity] ?? [];
      const available = items.filter((it) => (pool[it.slug] ?? 0) > 0);
      const candidates = available.length > 0 ? available : items;
      if (candidates.length === 0) continue;

      const item = candidates[Math.floor(Math.random() * candidates.length)];
      shopItems.push({
        Item: {
          Slug: item.slug,
          SellValue: item.cost - 1,
          Level: 1,
          Xp: 0,
          NextLevelXp: 1,
        },
        Cost: item.cost,
      });
    }
    options.push({ CurrentShop: shopItems });
  }

  return options;
}
