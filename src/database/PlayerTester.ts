import express from "express";
import "reflect-metadata";
import mongoose from "mongoose";
import { getModelForClass, prop, modelOptions, Severity } from "@typegoose/typegoose";
import { randomUUID } from "crypto";
import * as SharedTypes from "../types/shared-types";
import { str } from "dot-object";

const defaultToken = new SharedTypes.AccountToken() as SharedTypes.IAccountToken;

// Define the PlayerTester model
@modelOptions({ options: { allowMixed: Severity.ALLOW } })
export class PlayerTester {
  @prop({ default: "" })
  public name!: string;

  @prop({ default: "" })
  public hydraUsername!: string;

  // NOTE: `ip` is NOT unique — multiple accounts can share an IP (household NAT,
  // VPN, corporate network). Identity is disambiguated by steamId/epicId/hardwareId.
  // Indexed (non-unique) for IP-based fallback lookups in admin/HTML routes.
  // Empty when the IP link was released (see staleIpLinkFilter).
  @prop({ default: "", index: true })
  public ip!: string;

  // @prop({ required: false, unique: true })
  // public game_install!: SharedTypes.IGameInstall;

  // @prop({ required: false, unique: true })
  // public platform_id!: string;

  // @prop({ required: false, unique: true })
  // public platform_name!: string;

  // @prop({ required: false, unique: true })
  // public hydra_public_id!: string;

  @prop({ required: false, unique: false, default: () => defaultToken })
  public token!: SharedTypes.IAccountToken;

  @prop({ required: false, unique: false, default: () => defaultToken })
  public account!: SharedTypes.IAccountToken;

  @prop({ required: false, unique: false, default: 964 })
  public GameplayPreferences!: number;

  // MongoDB will auto-generate _id for each document
  @prop({ default: () => new mongoose.Types.ObjectId(), unique: true })
  public profile_id!: mongoose.Types.ObjectId;

  @prop({ default: () => randomUUID(), unique: true })
  public public_id!: string;

  @prop({ default: "profile_icon_default" })
  public profile_icon!: string;

  @prop({ required: false, unique: false, default: [] })
  public blockedPlayers!: string[];

  @prop({ default: "character_shaggy" })
  public character!: string;

  @prop({ default: "skin_shaggy_default" })
  public variant!: string;

  @prop({ default: "" })
  public party_key!: string;

  @prop({ default: "", index: true, sparse: true })
  public steamId!: string;

  @prop({ default: "", index: true, sparse: true })
  public epicId!: string;

  @prop({ default: "", index: true, sparse: true })
  public hardwareId!: string;

  @prop({ default: "" })
  public hardwareIdVersion!: string;

  @prop({ default: "" })
  public hardwareIdQuality!: string;

  // Canonical fallback for archive/offline builds which do not expose a Steam
  // or Epic account. Generated randomly by the DLL and persisted per install.
  @prop({ default: "", index: true })
  public installId!: string;

  // IP is connection metadata. These timestamps make old, recycled-IP links
  // age out of browser/account-picking compatibility flows after seven days.
  @prop({ default: () => new Date(), index: true })
  public lastSeenAt!: Date;

  @prop({ default: () => new Date(), index: true })
  public ipSeenAt!: Date;
  // Created for a login that carried no Steam/Epic/install id and matched no account
  // (an outdated client that only needs to see the update popup). One per IP, reused,
  // never used for IP recovery, and adopted (flag cleared) once that device sends an id.
  @prop({ default: false, index: true })
  public provisional!: boolean;
}

export const PlayerTesterModel = getModelForClass(PlayerTester);
