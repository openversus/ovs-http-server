import { getCounters } from "./playerCounters";

// Legacy hardcoded constant — kept for any caller that doesn't yet have an
// account context. New code should prefer `getToastInventoryEntry(accountId)`.
// Note: count was 9998 forever, served to every player identically.
export const ToastData = {
  item_slug: "match_toasts",
  count: 9998,
  result_type: "simple",
  currency_sources: [
    {
      source_slug: null,
      total_spent: 1200,
      total_earned: 11578,
      total_refunded: 0,
      should_expire: false,
      expires_at: null,
      purchase_id: null,
      source_platform: null,
    },
  ],
  data: {},
  server_data: {},
  updated_at: { _hydra_unix_date: 1741927542 },
  created_at: { _hydra_unix_date: 1721975525 },
};

/**
 * Builds the per-account match_toasts inventory entry served via
 * /profiles/:id/inventory. Pulls the real balance from PlayerCounters
 * (auto-created at 100 on first access).
 */
export async function getToastInventoryEntry(accountId: string) {
  const counters = await getCounters(accountId);
  return {
    ...ToastData,
    count: counters.match_toasts,
    updated_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
  };
}
