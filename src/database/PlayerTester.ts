import express from "express";
import "reflect-metadata";
import mongoose from "mongoose";
import { getModelForClass, prop, modelOptions, Severity } from "@typegoose/typegoose";
import { randomUUID } from "crypto";
import * as SharedTypes from "../types/shared-types";

const defaultToken = new SharedTypes.AccountToken() as SharedTypes.IAccountToken;

// Define the PlayerTester model
@modelOptions({ options: { allowMixed: Severity.ALLOW } })
export class PlayerTester {
  @prop({ default: "" })
  public name!: string;

  @prop({ required: true, unique: true })
  public ip!: string;

  @prop({ required: false, unique: true, default: () => defaultToken })
  public token!: SharedTypes.IAccountToken;

  @prop({ required: false, unique: true, default: () => defaultToken })
  public account!: SharedTypes.IAccountToken;

  // MongoDB will auto-generate _id for each document
  @prop({ default: () => new mongoose.Types.ObjectId(), unique: true })
  public profile_id!: mongoose.Types.ObjectId;

  @prop({ default: () => randomUUID(), unique: true })
  public public_id!: string;

  @prop({ default: "profile_icon_default" })
  public profile_icon!: string;

  @prop({ default: "character_shaggy" })
  public character!: string;

  @prop({ default: "skin_shaggy_default" })
  public variant!: string;
}

export const PlayerTesterModel = getModelForClass(PlayerTester);
