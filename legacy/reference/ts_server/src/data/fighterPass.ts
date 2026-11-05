// Fighter Pass (character mastery) layout shared by every Chromium track.
//
// Each completed ranked set on a character earns its Fighter Pass 400 XP, +200
// for winning the set, so ~500 at an even win rate. Custom games earn nothing.
// The Chromium capstone sits at 25,000 XP = ~50 sets (42 all wins, 63 all
// losses). Tier gaps widen from 1 to 6 sets as the pass goes on.

export const FIGHTER_PASS_TOAST_SLUG = "match_toasts";
export const FIGHTER_PASS_AVERAGE_SET_XP = 500;

export type FighterPassReward =
  | { kind: "toasts"; count: number }
  | { kind: "battlepassXp"; rewardHsda: "reward_xp_battlepass_600" | "reward_xp_battlepass_700" }
  | { kind: "chromium" };

// Index 0 is tier 1. Tiers 5, 10 and 15 feed the seasonal battle pass, and
// together are worth exactly one battle-pass tier (600 + 700 + 700 = 2,000 XP).
export const FIGHTER_PASS_TIERS: ReadonlyArray<{ threshold: number; reward: FighterPassReward }> = [
  { threshold: 1, reward: { kind: "toasts", count: 10 } },
  { threshold: 500, reward: { kind: "toasts", count: 10 } },
  { threshold: 1000, reward: { kind: "toasts", count: 15 } },
  { threshold: 2000, reward: { kind: "toasts", count: 15 } },
  { threshold: 3000, reward: { kind: "battlepassXp", rewardHsda: "reward_xp_battlepass_600" } },
  { threshold: 4000, reward: { kind: "toasts", count: 20 } },
  { threshold: 5500, reward: { kind: "toasts", count: 20 } },
  { threshold: 7000, reward: { kind: "toasts", count: 25 } },
  { threshold: 8500, reward: { kind: "toasts", count: 25 } },
  { threshold: 10500, reward: { kind: "battlepassXp", rewardHsda: "reward_xp_battlepass_700" } },
  { threshold: 12500, reward: { kind: "toasts", count: 30 } },
  { threshold: 14500, reward: { kind: "toasts", count: 30 } },
  { threshold: 17000, reward: { kind: "toasts", count: 40 } },
  { threshold: 19500, reward: { kind: "toasts", count: 50 } },
  { threshold: 22500, reward: { kind: "battlepassXp", rewardHsda: "reward_xp_battlepass_700" } },
  { threshold: 25000, reward: { kind: "chromium" } },
];

const BATTLEPASS_XP_REWARD = /^reward_xp_battlepass_(\d+)$/;

/** Battle-pass XP carried by a RewardTableLookup reward, or 0 when it is not one. */
export function battlepassXpForReward(rewardHsda: string | undefined): number {
  const match = rewardHsda ? BATTLEPASS_XP_REWARD.exec(rewardHsda) : null;
  return match ? Number(match[1]) : 0;
}
