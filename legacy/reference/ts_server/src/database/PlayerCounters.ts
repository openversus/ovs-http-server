// Per-account counter sidecar collection.
//
// Lives alongside the heavy Player model rather than inside it because:
//   - The Player model is a typed Typegoose tree with strict subdoc shapes;
//     adding new countable fields requires schema migrations and disturbs
//     unrelated callers (toJSON virtuals, flatten utilities, etc).
//   - Counters are independently updated on hot paths (toast give/receive,
//     daily login grants, future BP XP grants) — an atomic $inc on a small
//     dedicated doc is cheaper than touching the player blob.
//
// New counter fields should be added here as we expand the economy. The
// daily-login grant tracker (`lastToastBonusUnix`) is intentionally generic-
// enough that we can pattern-match it for BP XP daily grants too.

import { getModelForClass, modelOptions, prop, Severity } from "@typegoose/typegoose";

@modelOptions({ options: { allowMixed: Severity.ALLOW }, schemaOptions: { timestamps: true } })
export class PlayerCounters {
  // Player.id (the public string id, not the Mongo _id), unique per account.
  // Indexed for the hot-path read on every inventory request.
  @prop({ required: true, unique: true, index: true })
  public accountId!: string;

  // Spendable "match_toasts" balance — was hardcoded as 9998 in src/data/toast.ts
  // for every player; this field replaces that with per-account state.
  @prop({ required: true, default: 100 })
  public match_toasts!: number;

  // Unix seconds of the most recent claimed daily-toast bonus boundary.
  // The boundary is the most-recent 11:00 America/Chicago tick (handles DST).
  // 0 means "never claimed" — first eligible login grants the bonus.
  @prop({ required: true, default: 0 })
  public lastToastBonusUnix!: number;
}

export const PlayerCountersModel = getModelForClass(PlayerCounters);
