import { getModelForClass, prop, modelOptions, index, Severity } from "@typegoose/typegoose";

@modelOptions({ options: { allowMixed: Severity.ALLOW } })
@index({ account_id: 1 }, { unique: true })
@index({ elo_1v1: -1 })
@index({ elo_2v2: -1 })
export class EloRating {
  @prop({ required: true })
  public account_id!: string;

  @prop({ default: "" })
  public username!: string;

  @prop({ default: 1000 })
  public elo_1v1!: number;

  @prop({ default: 1000 })
  public elo_2v2!: number;

  @prop({ default: 0 })
  public wins_1v1!: number;

  @prop({ default: 0 })
  public losses_1v1!: number;

  @prop({ default: 0 })
  public wins_2v2!: number;

  @prop({ default: 0 })
  public losses_2v2!: number;

  // Character usage tracking: { "character_shaggy": 5, "character_batman": 3 }
  @prop({ type: () => Object, default: {} })
  public character_counts_1v1!: Record<string, number>;

  @prop({ type: () => Object, default: {} })
  public character_counts_2v2!: Record<string, number>;

  // Most-played character slug (derived from character_counts on each update)
  @prop({ default: "" })
  public top_character_1v1!: string;

  @prop({ default: "" })
  public top_character_2v2!: string;

  @prop({ default: () => Date.now() })
  public updated_at!: number;
}

export const EloRatingModel = getModelForClass(EloRating);
