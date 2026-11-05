import { SchemaOptions } from "mongoose";

const toJSONVirtualId: SchemaOptions["toJSON"] = {
  virtuals: true,
  versionKey: false,
  transform: function (doc, ret) {
    const output = ret as Record<string, unknown>;
    convertDatesToISO(output);
    delete output._id;
    delete output.__v;
  },
};

function convertDatesToISO(obj: Record<any, any>) {
  for (const key in obj) {
    if (obj.hasOwnProperty(key)) {
      if (obj[key] instanceof Date) {
        obj[key] = obj[key].toISOString();
      } else if (typeof obj[key] === "object" && obj[key] !== null) {
        convertDatesToISO(obj[key]);
      }
    }
  }
  return obj;
}

export default toJSONVirtualId;
