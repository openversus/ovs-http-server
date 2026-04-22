import { prop, modelOptions, getModelForClass, index, Severity } from "@typegoose/typegoose";

@modelOptions({ schemaOptions: { collection: "playerstats" }, options: { allowMixed: Severity.ALLOW } })
@index({ account_id: 1 }, { unique: true })
export class PlayerStats {
  @prop({ required: true })
  public account_id!: string;

  // Hot queue — last 10 matches per mode (FIFO via $push/$slice -10)
  @prop({ type: () => [Object], default: [] })
  public recent_matches_1v1!: RecentMatchEntry[];

  @prop({ type: () => [Object], default: [] })
  public recent_matches_2v2!: RecentMatchEntry[];

  // Per-character stats (badges, fighterStats, matchups, teammates) — split by mode
  @prop({ type: () => Object, default: {} })
  public characters_1v1!: Record<string, any>;

  @prop({ type: () => Object, default: {} })
  public characters_2v2!: Record<string, any>;

  // Aggregate combat stats — NOT split by mode. All Stat:Game:Character:* fields
  // incremented across every game regardless of character or mode.
  @prop({ type: () => Object, default: {} })
  public aggregate!: Record<string, number>;

  @prop({ default: () => Date.now() })
  public updated_at!: number;
}

// TypeScript interfaces for documentation — Mongo stores these as plain objects
export interface RecentMatchPlayerStats {
  accountId: string;
  character: string;
  teamIndex: number;
  damage: number;
  ringouts: number;
  deaths: number;
  isWinner: boolean;
}

export interface RecentMatchEntry {
  matchId: string;
  timestamp: number;
  mode: string;
  map: string;
  result: "win" | "loss";
  score: [number, number];
  players: RecentMatchPlayerStats[];
}

export interface MatchupCounters {
  wins: number;
  losses: number;
  upsets: number;
  chokes: number;
  dodges: number;
  tossupWins: number;
  tossupLosses: number;
}

export interface TeammateCounters {
  wins: number;
  losses: number;
  upsets: number;
  chokes: number;
}

export interface CharacterStats {
  // Set-level win/loss totals (include dodges)
  wins: number;
  losses: number;
  // Pregame dodge tracking — only counts dodges that happened BEFORE the match
  // actually started (no game played). Mid-match alt-F4 or between-games leaves
  // are real set wins/losses, not dodges.
  //   actualPlayedWins = wins - dodgeWins
  //   actualPlayedLosses = losses - dodgeLosses
  dodgeWins: number;       // wins where opponent dodged pregame
  dodgeLosses: number;     // losses where YOU dodged pregame
  // Character-level skill-based outcome counters (aggregated across all matchups)
  upsets: number;          // wins as underdog (expected < 0.44)
  chokes: number;          // losses as favorite (expected > 0.56)
  tossupWins: number;      // wins in even matchups (expected ≈ 0.5)
  tossupLosses: number;    // losses in even matchups
  // Game-level badges (accumulated)
  ringouts: number;
  totalDamageDealt: number;
  // Per-match MAX (updated via $max)
  highestDamageDealt: number;
  // Character-specific ability stats (Fighter:{X}:*, Hitbox:{X}:*, Marceline:*)
  fighterStats: Record<string, number>;
  // Outcomes vs each opponent character (same skill breakdown, per-opponent)
  matchups: Record<string, MatchupCounters>;
  // 2v2 only: synergy outcomes with each teammate character
  teammates?: Record<string, TeammateCounters>;
}

export const PlayerStatsModel = getModelForClass(PlayerStats);
