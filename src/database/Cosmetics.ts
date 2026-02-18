import { logger } from "../config/logger";
import { prop, modelOptions, getModelForClass, Ref, Severity, pre, post } from "@typegoose/typegoose";
import { PlayerTester } from "./PlayerTester";
import { Types } from "mongoose";
import ObjectID from "bson-objectid";

const serviceName: string = "Database.Cosmetics";

export interface IDefaultTaunts {
  [character: string]: {
    TauntSlots: string[];
  };
}

export const defaultTaunts: IDefaultTaunts = {
  character_wonder_woman: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_harleyquinn: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_taz: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_steven: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_c16: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_bugs_bunny: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "taunt_bugs_dance",
      "emote_beckon",
    ],
  },
  character_shaggy: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C028: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_creature: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_finn: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_BananaGuard: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_Jason: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_arya: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_tom_and_jerry: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C017: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_superman: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_velma: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C023A: {
    TauntSlots: [
      "taunt_c023A_default",
      "emote_generic_boom",
      "taunt_c023A_umbrella",
      "emote_shaggy_cry",
    ],
  },
  character_batman: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C023B: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C018: {
    TauntSlots: [
      "taunt_c018_Trumpet",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_jake: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C021: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C020: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_garnet: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_c019: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_c036: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C026: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C030: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_c024: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C027: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C031: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C025: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_c038: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
  character_C029: {
    TauntSlots: [
      "emote_rock_on",
      "emote_generic_heart",
      "emote_c023a_mogwhy",
      "emote_beckon",
    ],
  },
};

export class TauntSlotsClass {
  @prop({ type: [String], default: [] })
  public TauntSlots!: string[];
}
// Subfield for StatTrackers
class StatTrackersClass {
  @prop({
    type: () => [String],
    default: [],
  })
  public StatTrackerSlots!: string[];
}

// Subfield for Gems
class GemsClass {
  @prop({
    type: () => [String],
    default: [],
  })
  public GemSlots!: string[];
}

@pre<Cosmetics>("validate", function (next) {
  // Log every validation attempt for debugging
  logger.info(`[${serviceName}] PRE-VALIDATE called for document with _id: ${this._id}, account_id: ${this.account_id}`);

  if (!this.account_id && this._id) {
    logger.warn(`[${serviceName}] PRE-VALIDATE: account_id attribute is missing, setting it from _id: ${this._id}`);
    this.account_id = this._id as any;
  }

  if (this.account_id && !this._id) {
    logger.warn(`[${serviceName}] PRE-VALIDATE: _id attribute is missing, setting it from account_id: ${this.account_id}`);
    this._id = new Types.ObjectId(this.account_id.toString());
  }

  // If BOTH are missing, this is a real error - log the stack trace
  if (!this.account_id && !this._id) {
    logger.error(`[${serviceName}] PRE-VALIDATE ERROR: Both account_id and _id are missing!`);
    logger.error(`[${serviceName}] Document fields: ${JSON.stringify(this.toObject(), null, 2)}`);
    console.error(`[${serviceName}] CRITICAL: Stack trace where validation was triggered:`);
    console.trace();
  }

  next();
})
// Add pre('save') hook as well
@pre<Cosmetics>("save", function (next) {
  logger.info(`[${serviceName}] PRE-SAVE called for document with _id: ${this._id}, account_id: ${this.account_id}`);

  if (!this.account_id && this._id) {
    logger.warn(`[${serviceName}] PRE-SAVE: account_id was missing, setting it from _id: ${this._id}`);
    this.account_id = this._id as any;
  }

  next();
})
// Add hook for findOneAndUpdate operations with upsert
@pre<Cosmetics>("findOneAndUpdate", function (next) {
  const update = (this as any).getUpdate() as any;
  const options = (this as any).getOptions();

  // Log the operation
  logger.info(`[${serviceName}] PRE-findOneAndUpdate called with upsert: ${options.upsert}`);

  // If this is an upsert operation, ensure account_id is set
  if (options.upsert) {
    const filter = (this as any).getFilter();
    logger.info(`[${serviceName}] PRE-findOneAndUpdate filter: ${JSON.stringify(filter)}`);
    logger.info(`[${serviceName}] PRE-findOneAndUpdate update: ${JSON.stringify(update)}`);

    // If _id is in the filter and account_id is not being set, add it
    if (filter._id && !update.$set?.account_id && !update.$setOnInsert?.account_id) {
      logger.warn(`[${serviceName}] PRE-findOneAndUpdate: Adding account_id to $setOnInsert from filter._id: ${filter._id}`);
      if (!update.$setOnInsert) {
        update.$setOnInsert = {};
      }
      update.$setOnInsert.account_id = filter._id;
      (this as any).setUpdate(update);
    }
  }

  next();
})
@modelOptions({ schemaOptions: { collection: "cosmetics" }, options: { allowMixed: Severity.ALLOW } })
export class Cosmetics {
  @prop({ required: true, ref: () => PlayerTester })
  public account_id!: Ref<PlayerTester>;

  @prop({
    type: () => Object,
    // default: () => ({}),
    default: () => defaultTaunts,
    _id: false,
  })
  public Taunts!: Record<string, TauntSlotsClass>;

  @prop({ required: true, default: "announcer_pack_default" })
  public AnnouncerPack!: string;

  @prop({ required: true, default: "banner_default" })
  public Banner!: string;

  @prop({
    _id: false,
    default: () => ({
      StatTrackerSlots: [
        "stat_tracking_bundle_default",
        "stat_tracking_bundle_default",
        "stat_tracking_bundle_default",
      ],
    }),
  })
  public StatTrackers!: StatTrackersClass;

  @prop({ required: true, default: "ring_out_vfx_default" })
  public RingoutVfx!: string;

  @prop({
    _id: false,
    default: () => ({
      GemSlots: [
        "",
        "",
        "",
      ],
    }),
  })
  public Gems!: GemsClass;
}

export const CosmeticsModel = getModelForClass(Cosmetics);

// CosmeticsModel.schema.pre('validate', function(next) {
//   // Log every validation attempt for debugging
//   logger.info(`[${serviceName}] PRE-VALIDATE called for document with _id: ${this._id}, account_id: ${this.account_id}`);

//   if (!this.account_id && this._id) {
//     logger.warn(`[${serviceName}] PRE-VALIDATE: account_id attribute is missing, setting it from _id: ${this._id}`);
//     this.account_id = this._id as any;
//   }

//   if (this.account_id && !this._id) {
//     logger.warn(`[${serviceName}] PRE-VALIDATE: _id attribute is missing, setting it from account_id: ${this.account_id}`);
//     this._id = new Types.ObjectId(this.account_id.toString());
//   }

//   // If BOTH are missing, this is a real error - log the stack trace
//   if (!this.account_id && !this._id) {
//     logger.error(`[${serviceName}] PRE-VALIDATE ERROR: Both account_id and _id are missing!`);
//     logger.error(`[${serviceName}] Document fields: ${JSON.stringify(this.toObject(), null, 2)}`);
//     console.error(`[${serviceName}] CRITICAL: Stack trace where validation was triggered:`);
//     console.trace();
//   }

//   next();
// });

// CosmeticsModel.schema.pre('validate', function(next) {
//   if (!this.account_id && this._id) {
//     logger.warn(`[${serviceName}] PRE-VALIDATE: account_id attribute is missing, setting it from _id: ${this._id}`);
//     this.account_id = this._id as any;
//   }

//   if (this.account_id && !this._id) {
//     logger.warn(`[${serviceName}] PRE-VALIDATE: _id attribute is missing, setting it from account_id: ${this.account_id}`);
//     this._id = new Types.ObjectId(this.account_id.toString());
//   }

//   if (!this.account_id && !this._id) {
//     logger.error(`[${serviceName}] PRE-VALIDATE ERROR: Both account_id and _id are missing!`);
//     logger.error(`[${serviceName}] Document fields: ${JSON.stringify(this.toObject(), null, 2)}`);
//     console.trace(`[${serviceName}] Full stack trace: ${new Error().stack}`);
//   }

//   next();
// });

// CosmeticsModel.schema.pre('save', function(next) {
//   if (!this.account_id && this._id) {
//     logger.warn(`[${serviceName}] PRE-SAVE: account_id was missing, setting it from _id: ${this._id}`);
//     this.account_id = this._id;
//   }

//   next();
// });
