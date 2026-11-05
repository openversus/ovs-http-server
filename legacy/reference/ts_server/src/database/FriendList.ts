import { getModelForClass, modelOptions, prop, Severity } from "@typegoose/typegoose";
import mongoose from "mongoose";

@modelOptions({ schemaOptions: { _id: false } })
export class FriendEntry {
  @prop({ required: true })
  friendAccountId!: string;

  @prop({ required: true })
  friendUsername!: string;

  @prop({ default: "active", enum: ["active", "blocked"] })
  status!: string;

  @prop({ default: () => new Date() })
  addedAt!: Date;
}

@modelOptions({ options: { allowMixed: Severity.ALLOW } })
export class FriendList {
  _id!: mongoose.Types.ObjectId;

  @prop({ required: true, unique: true, index: true })
  accountId!: string;

  @prop({ type: () => [FriendEntry], default: [] })
  friends!: FriendEntry[];
}

export const FriendListModel = getModelForClass(FriendList);
