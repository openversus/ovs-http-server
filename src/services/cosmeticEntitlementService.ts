import env from "../env/env";
import { CHROMIUM_SKIN_SLUG_SET } from "../data/chromiumSkins";
import { OVS_BATTLEPASS_REWARD_SLUGS } from "../data/milestones";
import { getOwnedBattlepassSlugs, getOwnedFighterPassSlugs } from "../data/rewardTracks";
import { OVS_DEV_BADGE_SLUG } from "../data/ovsDevBadge";

const battlepassRestrictedSlugs = new Set<string>(OVS_BATTLEPASS_REWARD_SLUGS);

function accountList(value: string): Set<string> {
  return new Set(value.split(",").map((id) => id.trim()).filter(Boolean));
}

/** Whether the account owns the OVS Dev badge (OVS_DEV_ACCOUNT_IDS). */
export function isOvsDevAccount(accountId: string): boolean {
  return accountList(env.OVS_DEV_ACCOUNT_IDS).has(accountId);
}

export function filterOwnedByDefaultSlugs(slugs: string[]): string[] {
  return slugs.filter((slug) =>
    !battlepassRestrictedSlugs.has(slug)
    && !CHROMIUM_SKIN_SLUG_SET.has(slug)
    && slug !== OVS_DEV_BADGE_SLUG);
}

export async function filterInventoryForEntitlements<T extends { item_slug?: string }>(
  inventory: T[],
  accountId: string,
): Promise<T[]> {
  const [ownedBattlepass, ownedFighterPass] = await Promise.all([
    getOwnedBattlepassSlugs(accountId),
    getOwnedFighterPassSlugs(accountId),
  ]);
  return inventory.filter((entry) => {
    if (!entry.item_slug) return true;
    if (battlepassRestrictedSlugs.has(entry.item_slug)) return ownedBattlepass.has(entry.item_slug);
    if (CHROMIUM_SKIN_SLUG_SET.has(entry.item_slug)) return ownedFighterPass.has(entry.item_slug);
    if (entry.item_slug === OVS_DEV_BADGE_SLUG) return isOvsDevAccount(accountId);
    return true;
  });
}
