import env from "../env/env";
import { CHROMIUM_SKIN_SLUG_SET, TOP25_CROWN_CIRCUIT_SHAGGY_SLUG } from "../data/chromiumSkins";
import { PURE_FIRE_SHAGGY_SLUG } from "../data/pureFireSkin";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "../data/milestones";
import { getOwnedBattlepassSlugs, getOwnedFighterPassSlugs } from "../data/rewardTracks";
import { OVS_DEV_BADGE_SLUG } from "../data/ovsDevBadge";

const rankRestrictedSlugs = new Set([TOP25_CROWN_CIRCUIT_SHAGGY_SLUG, PURE_FIRE_SHAGGY_SLUG]);
const battlepassRestrictedSlugs = new Set<string>(OVS_BATTLEPASS_REWARD_SLUGS);

function accountList(value: string): Set<string> {
  return new Set(value.split(",").map((id) => id.trim()).filter(Boolean));
}

function configuredAccounts(): Set<string> {
  return accountList(env.CROWN_CIRCUIT_TOP25_ACCOUNT_IDS);
}

/** Whether the account owns the OVS Dev badge (OVS_DEV_ACCOUNT_IDS). */
export function isOvsDevAccount(accountId: string): boolean {
  return accountList(env.OVS_DEV_ACCOUNT_IDS).has(accountId);
}

export function filterOwnedByDefaultSlugs(slugs: string[]): string[] {
  return slugs.filter((slug) =>
    !rankRestrictedSlugs.has(slug)
    && !battlepassRestrictedSlugs.has(slug)
    && !CHROMIUM_SKIN_SLUG_SET.has(slug)
    && slug !== OVS_DEV_BADGE_SLUG);
}

export async function hasCrownCircuitEntitlement(accountId: string): Promise<boolean> {
  if (configuredAccounts().has(accountId)) return true;
  if (!env.CROWN_CIRCUIT_TOP25_DYNAMIC_ENABLED) return false;

  // Keep the ranking model out of ordinary inventory startup/tests. It is only
  // loaded when the optional database-backed rank gate is explicitly enabled.
  const { getPlayerRank } = await import("./eloService.js");
  const [oneVsOne, twoVsTwo] = await Promise.all([
    getPlayerRank(accountId, "1v1"),
    getPlayerRank(accountId, "2v2"),
  ]);
  return [oneVsOne, twoVsTwo].some((entry) => entry !== null && entry.rank <= 25);
}

export async function filterInventoryForEntitlements<T extends { item_slug?: string }>(
  inventory: T[],
  accountId: string,
): Promise<T[]> {
  const [hasRankEntitlement, ownedBattlepass, ownedFighterPass] = await Promise.all([
    hasCrownCircuitEntitlement(accountId),
    getOwnedBattlepassSlugs(accountId),
    getOwnedFighterPassSlugs(accountId),
  ]);
  return inventory.filter((entry) => {
    if (!entry.item_slug) return true;
    if (rankRestrictedSlugs.has(entry.item_slug)) return hasRankEntitlement;
    if (battlepassRestrictedSlugs.has(entry.item_slug)) return ownedBattlepass.has(entry.item_slug);
    if (CHROMIUM_SKIN_SLUG_SET.has(entry.item_slug)) return ownedFighterPass.has(entry.item_slug);
    if (entry.item_slug === OVS_DEV_BADGE_SLUG) return isOvsDevAccount(accountId);
    return true;
  });
}
