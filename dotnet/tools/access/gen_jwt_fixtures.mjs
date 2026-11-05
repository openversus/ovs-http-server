// Writes tests/OpenVersus.Server.Core.Tests/Access/jwt-fixtures.json with the TS server's own jsonwebtoken, so the C#
// AccessTokens is checked against what the TS services sign and accept. Run from the repository root:
//   node dotnet/tools/access/gen_jwt_fixtures.mjs
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const jwt = require(process.cwd() + "/node_modules/jsonwebtoken");

const secret = "fixture-secret-0123456789abcdef0123456789";
const iat = 1790000000;
const account = {
  id: "6abb50e38bd27776e0b0f758",
  profile_id: "6abb50e38bd27776e0b0f759",
  public_id: "615f61e3-7b87-489d-99a7-95e1694576d6",
  wb_network_id: "6abb50e38bd27776e0b0f758",
  hydraUsername: "OpenVersus_4918560530074",
  username: "OpenVersus_4918560530074",
  current_ip: "198.51.100.1",
  lobby_id: "",
  GameplayPreferences: 964,
  steamId: "76561198000000001",
  epicId: "",
  hardwareId: "",
};
// jsonwebtoken takes iat from the payload when given, which pins the time; it stays in the claims' last place.
const sign = (claims, options = {}, key = secret) => jwt.sign({ ...claims, iat }, key, options);

// The TS server passes a bare number of seconds, else the ms-style string (see env.ts accessTokenExpiresIn).
const signed = [
  { name: "no lifetime", ttl: "", token: sign(account) },
  { name: "24h", ttl: "24h", token: sign(account, { expiresIn: "24h" }) },
  { name: "seconds", ttl: "120", token: sign(account, { expiresIn: 120 }) },
  { name: "minutes", ttl: "2m", token: sign(account, { expiresIn: "2m" }) },
  { name: "days", ttl: "7d", token: sign(account, { expiresIn: "7d" }) },
  { name: "accented name", ttl: "24h", token: sign({ ...account, username: "Zoë Ñandú" }, { expiresIn: "24h" }) },
];

const verified = (token, at) => {
  try {
    jwt.verify(token, secret, { clockTimestamp: at });
    return true;
  } catch {
    return false;
  }
};
const identifyClaims = { steamId: "76561198000000001", installId: "0123456789abcdef0123456789abcdef", hardwareIdVersion: 2, clientVersion: "2026.09.28.04", identityRegistered: "1" };
const checks = [
  { name: "valid", token: sign(account, { expiresIn: "24h" }), at: iat + 10 },
  { name: "expired", token: sign(account, { expiresIn: 120 }), at: iat + 120 },
  { name: "just before expiry", token: sign(account, { expiresIn: 120 }), at: iat + 119 },
  { name: "another secret", token: sign(account, {}, "another-secret-0123456789abcdef0123456789"), at: iat },
  { name: "not yet valid", token: sign(account, { notBefore: 60 }), at: iat + 30 },
  { name: "an identify token", token: sign(identifyClaims), at: iat },
  { name: "tampered", token: sign(account).replace(/\.[^.]+\./, "." + Buffer.from(JSON.stringify({ ...account, id: "x", iat })).toString("base64url") + "."), at: iat },
  { name: "garbage", token: "not.a.token", at: iat },
];

const out = path.join(process.cwd(), "dotnet/tests/OpenVersus.Server.Core.Tests/Access/jwt-fixtures.json");
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify({
  secret,
  iat,
  claims: account,
  signed,
  checks: checks.map((c) => ({ ...c, accepted: verified(c.token, c.at) })),
  // jsonwebtoken accepts HS384/HS512 with the same secret; AccessTokens does not (nothing signs them).
  hs512: { token: jwt.sign({ ...account, iat }, secret, { algorithm: "HS512" }), acceptedByTs: verified(jwt.sign({ ...account, iat }, secret, { algorithm: "HS512" }), iat) },
}, null, 1) + "\n");
console.log(`wrote ${path.relative(process.cwd(), out)}`);
