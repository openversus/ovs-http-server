// Makes tests/OpenVersus.Server.Core.Tests/Hydra/encoder-fixtures.json: crafted values run through mvs-dump's
// HydraEncoder, the encoder the TS server has answered the game with, so the C# encoder can be held to its exact bytes.
// Run from dotnet/: node tools/hydra/gen_fixtures.mjs (needs ../node_modules/mvs-dump, i.e. `npm ci` at the repo root).
import { HydraEncoder } from "../../../node_modules/mvs-dump/dist/hydra/encoder.js";
import fs from "fs";
import crypto from "crypto";

const long = (n, c = "x") => c.repeat(n);
const cases = {
  null: null, true: true, false: false,
  zero: 0, u8max: 255, u16min: 256, u16max: 65535, u32min: 65536, u32max: 4294967295, u64min: 4294967296,
  negative: -1, negativeBig: -123456789012, double: 1.5, negativeDouble: -0.25, forcedDouble: { _hydra_double: 5 },
  emptyString: "", ascii: "hello", unicode: "héllo 🎮 名前", char16: long(256), char16max: long(65535), char32: long(65536),
  emptyArray: [], array: [1, "a", true, null], array16: Array.from({ length: 256 }, (_, i) => i),
  emptyMap: {}, map: { a: 1, b: "two", c: [3], d: { e: null } },
  map16: Object.fromEntries(Array.from({ length: 256 }, (_, i) => [`k${i}`, i])),
  date: { when: { _hydra_unix_date: 1790640000 } },
  localization: { name: { localizations: { en: "Shaggy" } } },
  storeEnabled: { enabled: { _hydra_StoreEnabed: [true, false] } },
  fileReference: { file_reference: { _customType: "hydra_reference", value: { id: "abc123", slug: "keyart", url: "https://x/y.png" } } },
  calendar: { c: { _hydra_calendar: { default: 1, rendered: 2 } } },
  // Decode-only: zlib builds differ, so compressed bytes need not match; the value inside must.
  compressed: { data: { _hydra_compressed: { players: ["a", "b"], count: 2, nested: { deep: [1, 2, 3] } } } },
  nested: { players: [{ id: "p1", skill: 1200, tags: ["a", "b"], ratio: 0.5, since: { _hydra_unix_date: 1 } }] },
};
const compact = (v) => typeof v === "string" && v.length > 1000 && new Set(v).size === 1 ? { $repeat: v[0], count: v.length } : v;
const out = {};
for (const [name, value] of Object.entries(cases)) {
  for (const ws of [false, true]) {
    const e = new HydraEncoder(ws);
    try {
      e.encodeValue(value);
      const bytes = e.returnValue();
      // Huge outputs are pinned by length and SHA-256 instead of their bytes, and huge strings are written as
      // { "$repeat": char, "count": n }, so the file stays small; the test rebuilds them.
      out[ws ? `${name}#websocket` : name] = bytes.length > 4096
        ? { value: compact(value), length: bytes.length, sha256: crypto.createHash("sha256").update(bytes).digest("hex") }
        : { value, hex: bytes.toString("hex"), ...(name.startsWith("compressed") ? { decodeOnly: true } : {}) };
    } catch (err) {
      // What the TS server cannot send either: the C# encoder must refuse it too.
      out[ws ? `${name}#websocket` : name] = { value: compact(value), error: err.code ?? String(err) };
    }
  }
}
fs.writeFileSync("tests/OpenVersus.Server.Core.Tests/Hydra/encoder-fixtures.json", JSON.stringify(out, null, 1) + "\n");
console.log(Object.keys(out).length, "fixtures");
