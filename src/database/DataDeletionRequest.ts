import { prop, modelOptions, getModelForClass, index, Severity } from "@typegoose/typegoose";

export type DataDeletionStatus = "pending" | "processed" | "cancelled" | "rejected";

@modelOptions({
  schemaOptions: { collection: "datadeletionrequests", timestamps: true },
  options: { allowMixed: Severity.ALLOW },
})
@index({ status: 1, createdAt: -1 })
@index({ requestIp: 1, createdAt: -1 })
export class DataDeletionRequest {
  // ── Submitted by user ──
  /** In-game name the user provided (free-form, may collide; admin disambiguates). */
  @prop({ required: true })
  public requestedName!: string;

  /** Optional free-text reason. */
  @prop({ default: "" })
  public reason!: string;

  // ── Captured server-side ──
  /** Best-effort real IP at submission (X-Forwarded-For aware). */
  @prop({ required: true, index: true })
  public requestIp!: string;

  @prop({ default: "" })
  public requestUserAgent!: string;

  // ── Lifecycle ──
  @prop({ required: true, default: "pending", index: true })
  public status!: DataDeletionStatus;

  /** Admin handle / username that processed or rejected. */
  @prop({ default: "" })
  public processedBy!: string;

  @prop()
  public processedAt?: Date;

  /** Admin-only notes (visible in admin UI, never shown to the user). */
  @prop({ default: "" })
  public adminNotes!: string;

  // ── Optional admin disambiguation hint ──
  /**
   * Filled by admin when they identify the matching account, BEFORE processing.
   * Lets a second admin cross-check before the destructive step.
   */
  @prop({ default: "" })
  public matchedAccountId!: string;
}

export const DataDeletionRequestModel = getModelForClass(DataDeletionRequest);
