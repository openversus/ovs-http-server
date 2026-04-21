import { PlayerStatsModel } from "../database/PlayerStats";
import { EloRatingModel } from "../database/EloRating";
import { logger } from "../config/logger";

const serviceName = "Services.FunFacts";
const logPrefix = `[${serviceName}]:`;

export interface FunFact {
  title: string;
  message: string;
}

interface Ctx {
  stats: any;
  elo: any;
  agg: Record<string, number>;
  chars1v1: Record<string, any>;
  chars2v2: Record<string, any>;
  recent1v1: any[];
  recent2v2: any[];
}

type FactGen = (ctx: Ctx) => FunFact | null;

// ── helpers ────────────────────────────────────────────────────────────
function prettyNumber(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return "0";
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 10_000) return `${Math.floor(n / 1000)}K`;
  return Math.round(n).toLocaleString();
}

function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return "0 seconds";
  if (seconds < 60) return `${Math.round(seconds)} seconds`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"}`;
  const hours = Math.floor(minutes / 60);
  const remMin = minutes % 60;
  return remMin > 0 ? `${hours}h ${remMin}m` : `${hours} hour${hours === 1 ? "" : "s"}`;
}

function prettyChar(slug: string): string {
  if (!slug) return "Unknown";
  const clean = slug.replace(/^character_/, "");
  // Handle code names like C023A / C030 — leave as-is (likely internal codenames)
  if (/^[cC]\d/.test(clean)) return clean;
  return clean
    .replace(/_/g, " ")
    .replace(/\b\w/g, c => c.toUpperCase());
}

function sumOverChars(map: Record<string, any>, field: string): number {
  return Object.values(map || {}).reduce((sum: number, v: any) => sum + (v[field] || 0), 0);
}

/**
 * Sum a fighterStats ability count across EVERY character the player has played,
 * regardless of slug. Safer than hardcoding character_<name> since the game's
 * slug mapping is inconsistent (BananaGuard, C025, Harley, WonderWoman, etc.)
 * and the PMU only emits Fighter:<Char>:<Ability> when that ability was actually
 * used, which always maps to the player's current character.
 */
function findFighterStat(c: Ctx, abilityName: string): number {
  let total = 0;
  for (const stats of Object.values(c.chars1v1 || {})) total += Number((stats as any)?.fighterStats?.[abilityName] || 0);
  for (const stats of Object.values(c.chars2v2 || {})) total += Number((stats as any)?.fighterStats?.[abilityName] || 0);
  return total;
}

// ── fact generators ────────────────────────────────────────────────────
// Each returns FunFact or null when the underlying data isn't present/interesting.
// Bias toward ratios, comparisons, and roasts over raw counters.
const FACT_GENS: FactGen[] = [
  // === Accuracy / hit rate — the crown jewel, per user request ===
  (c) => {
    const used = c.agg.totalAttacksUsed || 0;
    const hit = c.agg.totalAttacksHit || 0;
    if (used < 200) return null;
    const pct = Math.round((hit / used) * 100);
    if (pct < 25) return { title: "Whiff lord", message: `Only ${pct}% of your swings connect. Stormtroopers are calling for advice.` };
    if (pct < 40) return { title: "Hit rate", message: `You land ${pct}% of your attacks. The other ${100 - pct}% are prayers.` };
    if (pct < 55) return { title: "Hit rate", message: `${pct}% of your swings land. Respectable. Ish.` };
    if (pct < 70) return { title: "Surgical", message: `${pct}% accuracy. Somebody's been practicing.` };
    return { title: "Aimbot?", message: `${pct}% of your attacks hit. We're filing a report.` };
  },
  (c) => {
    const used = c.agg.totalSpecialsUsed || 0;
    const hit = c.agg.totalSpecialsHit || 0;
    if (used < 50) return null;
    const pct = Math.round((hit / used) * 100);
    if (pct < 30) return { title: "Special delivery", message: `Only ${pct}% of your specials connect. They're called specials, not scatters.` };
    if (pct > 70) return { title: "Special delivery", message: `${pct}% of your specials land. Clean.` };
    return null;
  },

  // === Ringout K/D ===
  (c) => {
    const r = c.agg.totalRingouts || 0;
    const rr = c.agg.totalRingoutsReceived || 0;
    if (r + rr < 10) return null;
    const ratio = r / Math.max(rr, 1);
    const msg = ratio > 2 ? `You ring out ${ratio.toFixed(2)}× more than you get sent. Bully.`
              : ratio < 0.5 ? `You get rung out ${(1/ratio).toFixed(2)}× more than you ring out. Ouch.`
              : `Your ringout ratio is ${ratio.toFixed(2)} (${r}/${rr}).`;
    return { title: "Splat math", message: msg };
  },

  // === Parries — if present in PMU. Check a few possible key names. ===
  (c) => {
    const count = c.agg.totalParries
               || c.agg.totalAttacksParried
               || c.agg.totalParriesLanded
               || c.agg.totalSuccessfulParries
               || 0;
    if (count < 1) return null;
    if (count >= 100) return { title: "Timing god", message: `${prettyNumber(count)} parries. Your reactions are witchcraft.` };
    return { title: "Timing is everything", message: `You've parried ${prettyNumber(count)} attack${count === 1 ? "" : "s"}.` };
  },

  // === Defense vs offense identity ===
  (c) => {
    const hits = c.agg.totalHitsTaken || 0;
    const dodges = c.agg.totalDodgesUsed || 0;
    if (hits + dodges < 200) return null;
    const pct = Math.round((dodges / (hits + dodges)) * 100);
    if (pct >= 60) return { title: "Untouchable", message: `For every 10 times someone attacks you, you dodge ${Math.round(pct/10)}. Spiderman sense.` };
    if (pct <= 15) return { title: "Wall", message: `You only dodge ${pct}% of the time — you just eat the hits. Respect.` };
    return null;
  },
  (c) => {
    const dd = c.agg.totalDamageDodged || 0;
    if (dd < 500) return null;
    return { title: "Matrix mode", message: `You've dodged ${prettyNumber(dd)} damage. Money saved.` };
  },

  // === Playstyle callouts ===
  (c) => {
    const air = c.agg.totalAirDamageDealt || 0;
    const ground = c.agg.totalGroundDamageDealt || 0;
    if (air + ground < 1000) return null;
    const pct = Math.round((air / (air + ground)) * 100);
    if (pct >= 70) return { title: "Airborne menace", message: `${pct}% of your damage happens off the ground. Bird mode.` };
    if (pct <= 30) return { title: "Feet planted", message: `${100 - pct}% of your damage is grounded. Unwavering.` };
    return null;
  },
  (c) => {
    const normals = c.agg.totalNormalAttacksUsed || 0;
    const specials = c.agg.totalSpecialsUsed || 0;
    if (normals + specials < 200) return null;
    const pct = Math.round((specials / (normals + specials)) * 100);
    if (pct >= 50) return { title: "Special order", message: `Over half (${pct}%) of your attacks are specials. Tier list manipulation.` };
    if (pct <= 15) return { title: "Fundamentals only", message: `Only ${pct}% of your attacks are specials. Pure neutral merchant.` };
    return null;
  },
  (c) => {
    const ca = c.agg.totalChargeAttacksUsed || 0;
    const total = c.agg.totalAttacksUsed || 0;
    if (total < 200 || ca < 50) return null;
    const pct = Math.round((ca / total) * 100);
    if (pct >= 20) return { title: "Charger", message: `${pct}% of your attacks are charged. Patience is a weapon.` };
    return null;
  },
  (c) => {
    const fc = c.agg.totalFullyChargedAttacksHit || 0;
    if (fc < 5) return null;
    return { title: "Smash!", message: `You've landed ${prettyNumber(fc)} FULLY charged attacks. Each one's a crime scene.` };
  },

  // === Clutch / style ===
  (c) => {
    const stolen = c.agg.totalRingoutsWithLessDamage || 0;
    if (stolen < 3) return null;
    return { title: "Clutch gene", message: `You've stolen ${prettyNumber(stolen)} ringouts while behind on damage. Scammer.` };
  },
  (c) => {
    const low = c.agg.totalRingoutsEnemyLowPercent || 0;
    if (low < 3) return null;
    return { title: "Early exit", message: `You've sent ${prettyNumber(low)} opponents out at low percent. Disrespectful.` };
  },
  (c) => {
    const proj = c.agg.totalProjectileRingouts || 0;
    const total = c.agg.totalRingouts || 0;
    if (total < 10 || proj < 3) return null;
    const pct = Math.round((proj / total) * 100);
    if (pct >= 30) return { title: "Camper alert", message: `${pct}% of your kills come from projectiles. From a safe distance.` };
    return null;
  },

  // === Time-based roasts (only if time values are present + interesting) ===
  (c) => {
    if (c.agg.totalCrouchTime > 120) {
      return { title: "Crouched up", message: `You've crouched for ${formatDuration(c.agg.totalCrouchTime)}. Turtle activity detected.` };
    }
    return null;
  },
  (c) => {
    if (c.agg.totalWallHangTime > 30) {
      return { title: "Spiderman mode", message: `You've clung to walls for ${formatDuration(c.agg.totalWallHangTime)}. The stage isn't your friend.` };
    }
    return null;
  },
  (c) => {
    const air = c.agg.totalAirTime || 0;
    const walk = c.agg.totalWalkTime || 0;
    if (air + walk < 300) return null;
    if (air > walk * 1.5) return { title: "Gravity? Never heard of her", message: `You spend more time in the air than walking. ${formatDuration(air)} airborne vs ${formatDuration(walk)} walking.` };
    return null;
  },
  (c) => {
    if (c.agg.totalPlatformDropThroughs > 200) {
      return { title: "Dropthrough addict", message: `You've dropped through platforms ${prettyNumber(c.agg.totalPlatformDropThroughs)} times. The platforms are tired of you.` };
    }
    return null;
  },

  // === Ringout geography ===
  (c) => {
    const left = c.agg.totalLeftRingouts || 0;
    const right = c.agg.totalRightRingouts || 0;
    const down = c.agg.totalDownRingouts || 0;
    const up = c.agg.totalUpRingouts || 0;
    const total = left + right + down + up;
    if (total < 20) return null;
    const max = Math.max(left, right, down, up);
    if (max / total < 0.45) return null; // not skewed enough to be interesting
    const side = max === left ? "left" : max === right ? "right" : max === down ? "bottom (spike city)" : "top (upward slams)";
    const pct = Math.round((max / total) * 100);
    return { title: "Favorite exit", message: `${pct}% of your ringouts go off the ${side}. You have a side.` };
  },
  (c) => {
    const down = c.agg.totalDownRingouts || 0;
    const total = c.agg.totalRingouts || 0;
    if (total < 20 || down < 5) return null;
    const pct = Math.round((down / total) * 100);
    if (pct >= 25) return { title: "Spike specialist", message: `${pct}% of your kills are spikes. Brutal.` };
    return null;
  },

  // --- Character-based ---
  (c) => {
    const all: Record<string, any> = { ...c.chars1v1 };
    for (const [k, v] of Object.entries(c.chars2v2 || {})) {
      all[k] = all[k] ? { wins: (all[k].wins || 0) + (v as any).wins || 0, losses: (all[k].losses || 0) + (v as any).losses || 0 } : v;
    }
    const entries = Object.entries(all)
      .map(([k, v]: [string, any]) => ({ char: k, games: (v.wins || 0) + (v.losses || 0) }))
      .filter(e => e.games >= 3)
      .sort((a, b) => b.games - a.games);
    if (entries.length === 0) return null;
    return { title: "Main character", message: `${prettyChar(entries[0].char)} is your most-played fighter (${entries[0].games} games).` };
  },
  (c) => {
    const entries = Object.entries(c.chars1v1 || {})
      .map(([k, v]: [string, any]) => ({ char: k, wins: v.wins || 0 }))
      .filter(e => e.wins >= 3)
      .sort((a, b) => b.wins - a.wins);
    if (entries.length === 0) return null;
    return { title: "Workhorse", message: `${prettyChar(entries[0].char)} has carried you to ${entries[0].wins} wins in 1v1.` };
  },
  (c) => {
    // 1v1 win streak — use characters_1v1.*.streak (largest positive)
    const entries = Object.entries(c.chars1v1 || {})
      .map(([k, v]: [string, any]) => ({ char: k, streak: v.streak || 0 }))
      .filter(e => e.streak >= 3)
      .sort((a, b) => b.streak - a.streak);
    if (entries.length === 0) return null;
    return { title: "Hot streak", message: `You're on a ${entries[0].streak}-win streak with ${prettyChar(entries[0].char)}. Keep it rolling.` };
  },
  (c) => {
    // Losing streak — recent_matches_1v1 from the end until a win
    const recent = c.recent1v1 || [];
    let streak = 0;
    for (let i = recent.length - 1; i >= 0; i--) {
      if (recent[i].result === "loss") streak++;
      else break;
    }
    if (streak < 3) return null;
    return { title: "Rough stretch", message: `You've dropped ${streak} in a row. Next one's yours.` };
  },
  (c) => {
    const upsets = sumOverChars(c.chars1v1, "upsets");
    if (upsets < 1) return null;
    return { title: "Giant slayer", message: `You've pulled off ${upsets} upset win${upsets === 1 ? "" : "s"} as the underdog.` };
  },
  (c) => {
    const chokes = sumOverChars(c.chars1v1, "chokes");
    if (chokes < 1) return null;
    return { title: "Don't get comfy", message: `You've been upset ${chokes} time${chokes === 1 ? "" : "s"} as the favorite.` };
  },
  (c) => {
    const tw = sumOverChars(c.chars1v1, "tossupWins");
    const tl = sumOverChars(c.chars1v1, "tossupLosses");
    if (tw + tl < 5) return null;
    const pct = Math.round((tw / (tw + tl)) * 100);
    return { title: "Coin flip", message: `In even matchups you win ${pct}% of the time.` };
  },
  (c) => {
    // Most common 2v2 teammate character
    const counts: Record<string, number> = {};
    for (const stats of Object.values(c.chars2v2 || {})) {
      const teammates = (stats as any).teammates || {};
      for (const [teamChar, tc] of Object.entries(teammates)) {
        const games = ((tc as any).wins || 0) + ((tc as any).losses || 0);
        counts[teamChar] = (counts[teamChar] || 0) + games;
      }
    }
    const entries = Object.entries(counts).sort((a, b) => b[1] - a[1]);
    if (entries.length === 0 || entries[0][1] < 3) return null;
    return { title: "Duo of legend", message: `Your most common 2v2 partner runs ${prettyChar(entries[0][0])} (${entries[0][1]} games together).` };
  },
  (c) => {
    // Unique characters played
    const unique = new Set([...Object.keys(c.chars1v1 || {}), ...Object.keys(c.chars2v2 || {})]).size;
    if (unique < 3) return null;
    return { title: "Roster depth", message: `You've played ${unique} different fighters.` };
  },
  (c) => {
    // Most upsets with a single character
    const entries = Object.entries(c.chars1v1 || {})
      .map(([k, v]: [string, any]) => ({ char: k, upsets: v.upsets || 0 }))
      .filter(e => e.upsets >= 2)
      .sort((a, b) => b.upsets - a.upsets);
    if (entries.length === 0) return null;
    return { title: "Secret weapon", message: `You've upset ${entries[0].upsets} favorites with ${prettyChar(entries[0].char)}. Sleeper pick.` };
  },
  (c) => {
    // Win rate on main character
    const entries = Object.entries(c.chars1v1 || {})
      .map(([k, v]: [string, any]) => ({ char: k, w: v.wins || 0, l: v.losses || 0, g: (v.wins || 0) + (v.losses || 0) }))
      .filter(e => e.g >= 5)
      .sort((a, b) => b.g - a.g);
    if (entries.length === 0) return null;
    const main = entries[0];
    const pct = Math.round((main.w / main.g) * 100);
    if (pct >= 70) return { title: "Comfy pick", message: `${prettyChar(main.char)} wins ${pct}% of their 1v1s. Don't mess with what works.` };
    if (pct <= 30) return { title: "Tough love", message: `Your ${prettyChar(main.char)} win rate is ${pct}%. Yet you keep picking them. Respect the loyalty.` };
    return { title: "Main stat", message: `${prettyChar(main.char)}: ${main.w}W-${main.l}L (${pct}%).` };
  },
  (c) => {
    // Nightmare matchup — biggest negative win rate vs a specific character across all of yours
    const matchups: Record<string, { wins: number; losses: number }> = {};
    for (const stats of Object.values(c.chars1v1 || {})) {
      const m = (stats as any).matchups || {};
      for (const [opp, rec] of Object.entries(m)) {
        matchups[opp] = matchups[opp] || { wins: 0, losses: 0 };
        matchups[opp].wins += (rec as any).wins || 0;
        matchups[opp].losses += (rec as any).losses || 0;
      }
    }
    const worst = Object.entries(matchups)
      .map(([opp, r]) => ({ opp, wr: r.wins / Math.max(r.wins + r.losses, 1), games: r.wins + r.losses, w: r.wins, l: r.losses }))
      .filter(e => e.games >= 4 && e.wr <= 0.3)
      .sort((a, b) => a.wr - b.wr);
    if (worst.length === 0) return null;
    const w = worst[0];
    return { title: "Kryptonite", message: `${prettyChar(w.opp)} is your worst matchup: ${w.w}W-${w.l}L.` };
  },
  (c) => {
    // Bully matchup — best positive win rate
    const matchups: Record<string, { wins: number; losses: number }> = {};
    for (const stats of Object.values(c.chars1v1 || {})) {
      const m = (stats as any).matchups || {};
      for (const [opp, rec] of Object.entries(m)) {
        matchups[opp] = matchups[opp] || { wins: 0, losses: 0 };
        matchups[opp].wins += (rec as any).wins || 0;
        matchups[opp].losses += (rec as any).losses || 0;
      }
    }
    const best = Object.entries(matchups)
      .map(([opp, r]) => ({ opp, wr: r.wins / Math.max(r.wins + r.losses, 1), games: r.wins + r.losses, w: r.wins, l: r.losses }))
      .filter(e => e.games >= 4 && e.wr >= 0.7)
      .sort((a, b) => b.wr - a.wr);
    if (best.length === 0) return null;
    const w = best[0];
    return { title: "Free real estate", message: `You bully ${prettyChar(w.opp)}: ${w.w}W-${w.l}L. They hate to see you coming.` };
  },

  // === Pure jokes (still gated on real data — no 0 values) ===
  (c) => {
    // Marathon walker — contextualize the distance (rough: 3 seconds walk ≈ 5m ≈ 16ft)
    if (c.agg.totalWalkTime < 600) return null;
    const minutes = Math.round(c.agg.totalWalkTime / 60);
    const roughMiles = (c.agg.totalWalkTime / 300).toFixed(1); // very rough; just for flavor
    return { title: "Pedometer", message: `You've walked for ${minutes} minutes in combat. That's roughly ${roughMiles} miles of pacing.` };
  },
  (c) => {
    if (c.agg.totalDamageTaken > 10000) {
      return { title: "Pain tolerance", message: `${prettyNumber(c.agg.totalDamageTaken)} damage taken. Your mains must have good health insurance.` };
    }
    return null;
  },
  (c) => {
    if (c.agg.totalJumps > 1000 && c.agg.totalDoubleJumps > 500) {
      const ratio = (c.agg.totalDoubleJumps / c.agg.totalJumps).toFixed(2);
      if (Number(ratio) > 0.7) {
        return { title: "Air conditioner", message: `You double-jump after ${Math.round(Number(ratio) * 100)}% of your jumps. Just buy a jetpack.` };
      }
    }
    return null;
  },
  (c) => {
    if (c.agg.totalRingoutsReceived > 50) {
      return { title: "Frequent flyer", message: `${prettyNumber(c.agg.totalRingoutsReceived)} ringouts taken. The blast zone has a rewards program and you're gold tier.` };
    }
    return null;
  },
  (c) => {
    // Comedy of errors: high attacks used, low hit rate
    const used = c.agg.totalAttacksUsed || 0;
    const hit = c.agg.totalAttacksHit || 0;
    if (used < 1000) return null;
    const missed = used - hit;
    if (missed / used > 0.6) {
      return { title: "Wind up", message: `${prettyNumber(missed)} of your attacks missed. That's a lot of shadowboxing.` };
    }
    return null;
  },
  (c) => {
    // Air>walk ratio — another spin
    if (c.agg.totalAirTime > 0 && c.agg.totalWalkTime > 0) {
      const ratio = c.agg.totalAirTime / c.agg.totalWalkTime;
      if (ratio > 3) {
        return { title: "Aerophile", message: `You spend ${ratio.toFixed(1)}× more time in the air than walking. Pigeons are nervous.` };
      }
    }
    return null;
  },

  // --- Fighter stats (character-specific abilities) ---
  // Dynamic "signature move" fact — surfaces the highest-count fighterStat across
  // every character the player has played. Works for any character the PMU tracks.
  (c) => {
    const abilities: Array<{ char: string; name: string; count: number }> = [];
    const merged: Record<string, Record<string, number>> = {};
    for (const [char, stats] of Object.entries({ ...c.chars1v1, ...c.chars2v2 })) {
      const fs = (stats as any).fighterStats || {};
      merged[char] = merged[char] || {};
      for (const [name, count] of Object.entries(fs)) {
        merged[char][name] = (merged[char][name] || 0) + Number(count || 0);
      }
    }
    for (const [char, fs] of Object.entries(merged)) {
      for (const [name, count] of Object.entries(fs)) {
        if (count > 0) abilities.push({ char, name, count });
      }
    }
    abilities.sort((a, b) => b.count - a.count);
    if (abilities.length === 0) return null;
    const top = abilities[0];
    if (top.count < 3) return null;
    return {
      title: "Signature move",
      message: `You've used ${prettyChar(top.char)}'s ${top.name} ${prettyNumber(top.count)} times.`,
    };
  },

  // Specific character-ability jokes. Key names come from PMU tags in
  // missionObjectives.ts: "Fighter:<Char>:<Ability>" / "Hitbox:<Char>:<Ability>"
  // / "Marceline:<Ability>". extractFighterStatName strips the prefix before
  // storing on the player's current character. findFighterStat() scans across
  // ALL characters so we don't have to guess the character slug.
  //
  // === Banana Guard ===
  (c) => {
    const count = findFighterStat(c, "Buff");
    if (count < 1) return null;
    return { title: "Buff banana", message: `You've popped Banana Guard's buff ${prettyNumber(count)} times. Bodybuilder mode.` };
  },
  (c) => {
    const count = findFighterStat(c, "Facebox");
    if (count < 1) return null;
    return { title: "Slip 'n slide", message: `You've banana-slipped ${prettyNumber(count)} times. Straight onto the face.` };
  },
  // === Shaggy ===
  (c) => {
    const count = findFighterStat(c, "SandwichHeal");
    if (count < 1) return null;
    return { title: "Munchies", message: `You've eaten ${prettyNumber(count)} Shaggy sandwich${count === 1 ? "" : "es"}. Zoinks.` };
  },
  (c) => {
    const count = findFighterStat(c, "EnragedAttackUsed");
    if (count < 1) return null;
    return { title: "Like, RAGE mode", message: `Shaggy's gone full rage ${prettyNumber(count)} time${count === 1 ? "" : "s"}.` };
  },
  // === Garnet ===
  (c) => {
    const count = findFighterStat(c, "FullBuff");
    if (count < 1) return null;
    return { title: "Fusion complete", message: `Garnet has fully charged ${prettyNumber(count)} times. Electric.` };
  },
  // === Steven ===
  (c) => {
    const count = findFighterStat(c, "Bubblestack");
    if (count < 1) return null;
    return { title: "Bubble wrap", message: `You've stacked Steven's bubble ${prettyNumber(count)} times.` };
  },
  (c) => {
    const count = findFighterStat(c, "EnemyBubble");
    if (count < 1) return null;
    return { title: "Caught you", message: `You've trapped ${prettyNumber(count)} enemies in Steven's bubble.` };
  },
  // === Velma ===
  (c) => {
    const count = findFighterStat(c, "Van");
    if (count < 1) return null;
    return { title: "Jinkies!", message: `Velma's van has flattened ${prettyNumber(count)} victim${count === 1 ? "" : "s"}.` };
  },
  (c) => {
    const count = findFighterStat(c, "GrabAlly");
    if (count < 1) return null;
    return { title: "Teamwork", message: `Velma's grabbed a teammate ${prettyNumber(count)} times. Rescue squad.` };
  },
  // === Joker ===
  (c) => {
    const count = findFighterStat(c, "Counter");
    if (count < 1) return null;
    return { title: "Why so serious?", message: `Joker has countered ${prettyNumber(count)} time${count === 1 ? "" : "s"}. Nailed the timing.` };
  },
  // === Jason ===
  (c) => {
    const count = findFighterStat(c, "RageHit");
    if (count < 1) return null;
    return { title: "Unstoppable force", message: `${prettyNumber(count)} rage hits with Jason. Horror movie material.` };
  },
  // === Harley ===
  (c) => {
    const count = findFighterStat(c, "BombSave");
    if (count < 1) return null;
    return { title: "Bombastic save", message: `Harley's bomb has bailed your teammate out ${prettyNumber(count)} times.` };
  },
  // === Iron Giant ===
  (c) => {
    const count = findFighterStat(c, "RageMission");
    if (count < 1) return null;
    return { title: "I am... weapon", message: `You've triggered Iron Giant's rage ${prettyNumber(count)} times.` };
  },
  // === Taz ===
  (c) => {
    const count = findFighterStat(c, "Eat");
    if (count < 1) return null;
    return { title: "Nom nom", message: `Taz has eaten ${prettyNumber(count)} opponent${count === 1 ? "" : "s"}. Dietary concerns.` };
  },
  // === Marceline ===
  (c) => {
    const count = findFighterStat(c, "Powerslide");
    if (count < 1) return null;
    return { title: "Bass drop", message: `Marceline's powerslid ${prettyNumber(count)} time${count === 1 ? "" : "s"}. Low-key stylish.` };
  },
  // === Agent Smith ===
  (c) => {
    const count = findFighterStat(c, "Silence");
    if (count < 1) return null;
    return { title: "Hush now", message: `Agent Smith has silenced ${prettyNumber(count)} opponent${count === 1 ? "" : "s"}. The Matrix has you.` };
  },
  (c) => {
    const count = findFighterStat(c, "TeleAlly");
    if (count < 1) return null;
    return { title: "Paging Mr. Anderson", message: `Agent Smith's teleported a teammate ${prettyNumber(count)} times.` };
  },
  // === LeBron ===
  (c) => {
    const count = findFighterStat(c, "Pass");
    if (count < 1) return null;
    return { title: "MVP assist", message: `${prettyNumber(count)} LeBron passes landed. Court vision.` };
  },
  (c) => {
    const count = findFighterStat(c, "Defense");
    if (count < 1) return null;
    return { title: "Rim protector", message: `${prettyNumber(count)} defensive stop${count === 1 ? "" : "s"} with LeBron.` };
  },
  // === Finn ===
  (c) => {
    const count = findFighterStat(c, "BuyBmo");
    if (count < 1) return null;
    return { title: "Mathematical", message: `Finn's bought BMO ${prettyNumber(count)} time${count === 1 ? "" : "s"}. Big spender.` };
  },
  (c) => {
    // Note the game's typo: "AirPojectileDestroy" (missing the 'r')
    const count = findFighterStat(c, "AirPojectileDestroy");
    if (count < 1) return null;
    return { title: "Swat flies", message: `Finn's swatted ${prettyNumber(count)} projectile${count === 1 ? "" : "s"} out of the air.` };
  },
  // === Batman ===
  (c) => {
    const count = findFighterStat(c, "Electric");
    if (count < 1) return null;
    return { title: "Zap!", message: `${prettyNumber(count)} electric batarang${count === 1 ? "" : "s"} with Batman. Shocking.` };
  },
  (c) => {
    const count = findFighterStat(c, "GrappleAlly");
    if (count < 1) return null;
    return { title: "I'm Batman", message: `Batman's grappled to a teammate ${prettyNumber(count)} times. Dynamic duo.` };
  },
  // === Rick ===
  (c) => {
    const count = findFighterStat(c, "PortalKB");
    if (count < 1) return null;
    return { title: "Wubba lubba", message: `${prettyNumber(count)} portal knockback${count === 1 ? "" : "s"}. Dimension-hopping dirty work.` };
  },
  (c) => {
    const count = findFighterStat(c, "AllySeed");
    if (count < 1) return null;
    return { title: "Plumbus care", message: `Rick has buffed a teammate ${prettyNumber(count)} times with a seed.` };
  },
  // === Morty ===
  (c) => {
    const count = findFighterStat(c, "SplitNade");
    if (count < 1) return null;
    return { title: "Aw jeez", message: `Morty has split ${prettyNumber(count)} grenade${count === 1 ? "" : "s"}. Risky moves.` };
  },
  (c) => {
    const count = findFighterStat(c, "AllySave");
    if (count < 1) return null;
    return { title: "Sidekick energy", message: `Morty's bailed out a teammate ${prettyNumber(count)} times.` };
  },
  // === Reindog ===
  (c) => {
    const count = findFighterStat(c, "LoveLeashAlly");
    if (count < 1) return null;
    return { title: "Heartstrings", message: `You've love-leashed your teammate ${prettyNumber(count)} times. Co-op goals.` };
  },
  // === Beetlejuice ===
  (c) => {
    const count = findFighterStat(c, "Teleport");
    if (count < 1) return null;
    return { title: "Say my name", message: `Beetlejuice has teleported ${prettyNumber(count)} time${count === 1 ? "" : "s"}. It's showtime.` };
  },
  // === Black Adam ===
  (c) => {
    const count = findFighterStat(c, "ProjectileShield");
    if (count < 1) return null;
    return { title: "Godly defense", message: `Black Adam has shielded ${prettyNumber(count)} projectile${count === 1 ? "" : "s"}. Shazam who?` };
  },
  // === Marvin ===
  (c) => {
    const count = findFighterStat(c, "ReverseProjectile");
    if (count < 1) return null;
    return { title: "Return to sender", message: `Marvin has reversed ${prettyNumber(count)} projectile${count === 1 ? "" : "s"}.` };
  },
  (c) => {
    const count = findFighterStat(c, "bubble");
    if (count < 1) return null;
    return { title: "Martian contained", message: `Marvin has bubbled ${prettyNumber(count)} opponent${count === 1 ? "" : "s"}.` };
  },
  // === Stripe ===
  (c) => {
    const count = findFighterStat(c, "Kickflip");
    if (count < 1) return null;
    return { title: "Gnarly", message: `Stripe has landed ${prettyNumber(count)} kickflip${count === 1 ? "" : "s"}. Shred it.` };
  },
  // === Jerry ===
  (c) => {
    const count = findFighterStat(c, "AllyAttach");
    if (count < 1) return null;
    return { title: "Piggyback", message: `Jerry has latched onto a teammate ${prettyNumber(count)} times.` };
  },
  // === C025 / Lenore ===
  (c) => {
    const count = findFighterStat(c, "lenoregrab");
    if (count < 1) return null;
    return { title: "Ghostly", message: `Lenore has grabbed ${prettyNumber(count)} opponent${count === 1 ? "" : "s"}.` };
  },

  // --- ELO ---
  (c) => {
    if (!c.elo || !c.elo.elo_1v1) return null;
    const wins = c.elo.wins_1v1 || 0;
    const losses = c.elo.losses_1v1 || 0;
    if (wins + losses < 5) return null;
    return { title: "Rating check", message: `Your 1v1 rating is ${c.elo.elo_1v1}. (${wins}W-${losses}L)` };
  },

  // --- Recent-match highlights ---
  (c) => {
    let best = 0;
    let bestChar = "";
    const aid = c.stats.account_id;
    for (const m of [...(c.recent1v1 || []), ...(c.recent2v2 || [])]) {
      for (const p of (m.players || [])) {
        if (p.accountId === aid && (p.damage || 0) > best) {
          best = p.damage;
          bestChar = p.character;
        }
      }
    }
    if (best < 50) return null;
    return { title: "Heavy hitter", message: `Your best damage in a recent game is ${best} with ${prettyChar(bestChar)}.` };
  },
  (c) => {
    let best = 0;
    const aid = c.stats.account_id;
    for (const m of [...(c.recent1v1 || []), ...(c.recent2v2 || [])]) {
      for (const p of (m.players || [])) {
        if (p.accountId === aid && (p.ringouts || 0) > best) best = p.ringouts;
      }
    }
    if (best < 2) return null;
    return { title: "Clean sweep", message: `You've put up ${best} ringouts in a single recent game.` };
  },
];

/**
 * Pick a random fun fact for the player, or null if there's not enough data.
 * Intentionally stateless — repeat facts across logins are fine per project direction.
 */
export async function getRandomFunFact(accountId: string): Promise<FunFact | null> {
  try {
    const [stats, elo] = await Promise.all([
      PlayerStatsModel.findOne({ account_id: accountId }).lean(),
      EloRatingModel.findOne({ account_id: accountId }).lean(),
    ]);

    if (!stats) {
      return null; // brand new player — skip
    }

    const ctx: Ctx = {
      stats,
      elo: elo || {},
      agg: (stats as any).aggregate || {},
      chars1v1: (stats as any).characters_1v1 || {},
      chars2v2: (stats as any).characters_2v2 || {},
      recent1v1: (stats as any).recent_matches_1v1 || [],
      recent2v2: (stats as any).recent_matches_2v2 || [],
    };

    const pool: FunFact[] = [];
    for (const gen of FACT_GENS) {
      try {
        const f = gen(ctx);
        if (f) pool.push(f);
      } catch (e) {
        // Individual fact gen errors are non-fatal — skip and continue
      }
    }

    if (pool.length === 0) return null;
    return pool[Math.floor(Math.random() * pool.length)];
  } catch (e) {
    logger.error(`${logPrefix} Error generating fact for ${accountId}: ${e}`);
    return null;
  }
}
