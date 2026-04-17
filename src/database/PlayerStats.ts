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
export interface RecentMatchEntry {
  matchId: string;
  timestamp: number;
  mode: string;
  myCharacter: string;
  opponentCharacter: string;
  teammateCharacter?: string; // 2v2 only
  result: "win" | "loss";
  score: [number, number];
  eloChange: number;
  eloBefore: number;
  opponentElo: number;
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
  // Set-level badges
  wins: number;
  losses: number;
  // Game-level badges (accumulated)
  ringouts: number;
  totalDamageDealt: number;
  // Per-match MAX (updated via $max)
  highestDamageDealt: number;
  // Character-specific ability stats (Fighter:{X}:*, Hitbox:{X}:*, Marceline:*)
  fighterStats: Record<string, number>;
  // Outcomes vs each opponent character
  matchups: Record<string, MatchupCounters>;
  // 2v2 only: synergy outcomes with each teammate character
  teammates?: Record<string, TeammateCounters>;
}

export const PlayerStatsModel = getModelForClass(PlayerStats);
