// Writes tests/OpenVersus.Server.Http.Tests/ip-fixtures.json: strings and whether Node's net.isIP (what the TS server
// uses to accept a forwarded client address) takes them, so ClientAddress.IsIPAddress is held to the same answers.
//   node dotnet/tools/access/gen_ip_fixtures.mjs
import fs from "node:fs";
import net from "node:net";

const fixed = [
  "1.2.3.4", "255.255.255.255", "256.1.1.1", "01.2.3.4", "1.2.3", "1.2.3.4.5", "1.2.3.4 ", " 1.2.3.4", "1.2.3.4\n", "0.0.0.0",
  "::", "::1", "1::", "2001:db8::1", "2001:DB8::1", "2001:0db8:0000:0000:0000:0000:0000:0001", "1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8:9",
  "1:2:3:4:5:6:7::", "::ffff:1.2.3.4", "::ffff:256.2.3.4", "1::2::3", "fe80::1%eth0", "fe80::1%", "fe80::1%25", "[::1]", "12345::",
  "::1.2.3.4", "1:2:3:4:5:6:1.2.3.4", "1:2:3:4:5:6:7:1.2.3.4", "g::1", ":1", "1:", "", "localhost", "prod.openversus.org",
  "198.51.100.7, 203.0.113.1", "0x1.2.3.4", "1.2.3.04", "١.2.3.4", "::ffff:0:1.2.3.4", "64:ff9b::1.2.3.4", "1:2:3:4:5::1.2.3.4",
];
// Random strings over the characters addresses are made of, so the two implementations are compared well beyond the
// cases anyone thought of.
// mulberry32: small, seeded, so the fixture is the same on every run.
let seed = 42;
const rand = (n) => {
  seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return (((t ^ (t >>> 14)) >>> 0) % n);
};
const alphabet = "0123456789abcdefABCDEF:.%";
const random = [];
for (let i = 0; i < 20000; i++) {
  let s = "";
  const len = 1 + rand(24);
  for (let j = 0; j < len; j++) s += alphabet[rand(alphabet.length)];
  random.push(s);
}
// Near-valid IPv6: valid forms with one group changed.
for (let i = 0; i < 5000; i++) {
  const groups = Array.from({ length: 8 }, () => rand(65536).toString(16));
  const cut = rand(8), len = rand(8 - cut);
  const text = len > 0 ? groups.slice(0, cut).join(":") + "::" + groups.slice(cut + len).join(":") : groups.join(":");
  random.push(rand(4) === 0 ? text + ":" + rand(99) : text);
}
// Near-valid IPv4: octets up to 300, sometimes zero-padded, sometimes a part short or long.
for (let i = 0; i < 5000; i++) {
  const parts = Array.from({ length: 3 + rand(3) }, () => {
    const v = rand(301);
    return rand(6) === 0 ? String(v).padStart(3, "0") : String(v);
  });
  random.push(parts.join("."));
  // Mixed IPv6 with an embedded IPv4.
  if (i % 5 === 0) random.push(`::ffff:${parts.slice(0, 4).join(".")}`);
}
const cases = [...new Set([...fixed, ...random])].map((s) => [s, net.isIP(s) !== 0]);
const out = "dotnet/tests/OpenVersus.Server.Http.Tests/ip-fixtures.json";
fs.writeFileSync(out, JSON.stringify(cases) + "\n");
console.log(`${cases.length} cases (${cases.filter((c) => c[1]).length} addresses) -> ${out}`);
