import { schema, types } from "papr";
import papr, { dbEmitter } from "../papr";

const rollbackServerSchema = schema(
  {
    regionId: types.string({ required: true }),
    ip: types.string({ required: true }),
    port: types.number({ required: true }),
    provider: types.string({ required: true }),
  },
  {
    timestamps: true,
  },
);

dbEmitter.once("connected", async () => {
  await RollbackServerModel.collection.createIndex({ regionId: 1 });
});

export type RollbackServer = (typeof rollbackServerSchema)[0];

export const RollbackServerModel = papr.model("rollbackservers", rollbackServerSchema);
