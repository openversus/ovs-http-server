import { getModelForClass, modelOptions, prop, Severity, index } from "@typegoose/typegoose";
import mongoose from "mongoose";

@index({ toAccountId: 1, status: 1 })
@index({ fromAccountId: 1, status: 1 })
@modelOptions({ options: { allowMixed: Severity.ALLOW } })
export class FriendRequest {
  _id!: mongoose.Types.ObjectId;

  @prop({ required: true })
  fromAccountId!: string;

  @prop({ required: true })
  fromUsername!: string;

  @prop({ required: true })
  toAccountId!: string;

  @prop({ required: true })
  toUsername!: string;

  @prop({ default: "pending", enum: ["pending", "accepted", "declined"] })
  status!: string;

  @prop({ default: () => new Date() })
  createdAt!: Date;

  @prop({ default: () => new Date() })
  updatedAt!: Date;
}

export const FriendRequestModel = getModelForClass(FriendRequest);
