import { prop, modelOptions, getModelForClass, index, Severity } from "@typegoose/typegoose";

@modelOptions({ schemaOptions: { collection: "match_archives" }, options: { allowMixed: Severity.ALLOW } })
@index({ match_id: 1 }, { unique: true })
@index({ timestamp: 1 }, { expireAfterSeconds: 7776000 }) // 90-day TTL
export class MatchArchive {
  @prop({ required: true })
  public match_id!: string;

  @prop({ required: true })
  public timestamp!: Date;

  // zstd-compressed JSON blob containing full set context:
  // players, characters, ELO before/after, scores, mode, etc.
  @prop({ required: true })
  public compressed_data!: Buffer;
}

export const MatchArchiveModel = getModelForClass(MatchArchive);
