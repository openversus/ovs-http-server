import mongoose, { Schema } from "mongoose";
import env from "../env/env";
import { Player, playerModel } from "./Player";
import UserSegment from "../enums/user_segment";
import { accountModel } from "./Account";
import { configurationModel } from "./Configuration";
import { logger } from "../config/logger";

const serviceName: string = "Database.Client";
const logPrefix = `[${serviceName}]:`;

export async function connect() {
  await mongoose.connect(env.MONGODB_URI);
  logger.info(`${serviceName}: Successfully connected to MongoDB!`);
}
