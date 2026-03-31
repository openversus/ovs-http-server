import { getModelForClass, prop, modelOptions, index, Severity } from "@typegoose/typegoose";

// Per-character ELO data
export interface CharacterElo {
  elo: number;
  wins: number;
  losses: number;
  streak: number;
}

@modelOptions({ options: { allowMixed: Severity.ALLOW } })
@index({ account_id: 1 }, { unique: true })
@index({ elo_1v1: -1 })
@index({ elo_2v2: -1 })
export class EloRating {
  @prop({ required: true })
  public account_id!: string;

  @prop({ default: "" })
  public username!: string;

  // Global ELO (best character's ELO — used for matchmaking + display)
  @prop({ default: 0 })
  public elo_1v1!: number;

  @prop({ default: 0 })
  public elo_2v2!: number;

  // Global win/loss totals (sum across all characters)
  @prop({ default: 0 })
  public wins_1v1!: number;

  @prop({ default: 0 })
  public losses_1v1!: number;

  @prop({ default: 0 })
  public wins_2v2!: number;

  @prop({ default: 0 })
  public losses_2v2!: number;

  @prop({ default: 0 })
  public win_streak_1v1!: number;

  @prop({ default: 0 })
  public win_streak_2v2!: number;

  // Per-character ELO: { "character_jake": { elo: 1200, wins: 10, losses: 5, streak: 3 }, ... }
  @prop({ default: {} })
  public characters_1v1!: Record<string, CharacterElo>;

  @prop({ default: {} })
  public characters_2v2!: Record<string, CharacterElo>;

  @prop({ default: () => Date.now() })
  public updated_at!: number;
}

export const EloRatingModel = getModelForClass(EloRating);
