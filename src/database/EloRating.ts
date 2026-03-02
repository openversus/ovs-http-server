import { getModelForClass, prop, modelOptions, index, Severity } from "@typegoose/typegoose";

@modelOptions({ options: { allowMixed: Severity.ALLOW } })
@index({ account_id: 1 }, { unique: true })
@index({ elo_1v1: -1 })
@index({ elo_2v2: -1 })
export class EloRating {
  @prop({ required: true, unique: true })
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

  @prop({ default: () => Date.now() })
  public updated_at!: number;
}

export const EloRatingModel = getModelForClass(EloRating);
