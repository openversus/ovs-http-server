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
  Slug: string;
  Gem: string;
  Cost: number;
  SellValue: number;
  Level: number;
  Xp: number;
  NextLevelXp: number;
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

// ─── Weighted selection ───────────────────────────────────────────────────────

function getItemWeight(item: ArenaItemDef, characterClass: number, round: number): number {
  const base = Math.max(0, item.startingWeight + (round - 1) * item.weightPerRound);
  const scalar = item.tagWeightScalars.find((s) => s.classIndex === characterClass);
  if (!scalar) return base;
  return Math.max(0, base * scalar.scalar + scalar.absolute);
}

function weightedSample<T>(items: T[], weights: number[]): T {
  const total = weights.reduce((a, b) => a + b, 0);
  let r = Math.random() * total;
  for (let i = 0; i < items.length; i++) {
    r -= weights[i];
    if (r <= 0) return items[i];
  }
  return items[items.length - 1];
}

// ─── Shop generation ──────────────────────────────────────────────────────────

/**
 * Generate `count` shop options, each containing `itemsPerOption` items.
 * Items can repeat across options (and within an option) to allow levelling.
 */
export function generateShopOptions(
  characterClass: number,
  round: number,
  count = 6,
  itemsPerOption = 4,
): ShopOption[] {
  const weights = ALL_ARENA_ITEMS.map((item) => getItemWeight(item, characterClass, round));
  const options: ShopOption[] = [];

  for (let i = 0; i < count; i++) {
    const shopItems: ShopItem[] = [];
    for (let j = 0; j < itemsPerOption; j++) {
      const item = weightedSample(ALL_ARENA_ITEMS, weights);
      shopItems.push({
        Slug: item.slug,
        Gem: item.gemName,
        Cost: item.cost,
        SellValue: item.cost - 1,
        Level: 1,
        Xp: 0,
        NextLevelXp: 1,
      });
    }
    options.push({ CurrentShop: shopItems });
  }

  return options;
}
