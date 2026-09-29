import assert from "node:assert/strict";
import test from "node:test";
import { clientIpFromHeaders } from "../src/utils/clientIp";

const SOCKET = "10.0.0.1"; // the reverse proxy, as the server sees the connection
const ip = (headers: Record<string, string | string[]>) => clientIpFromHeaders(headers, SOCKET).ip;

test("X-Real-IP from the proxy is the client", () => {
  assert.equal(ip({ "x-real-ip": "198.51.100.7" }), "198.51.100.7");
  assert.equal(ip({ "x-real-ip": "198.51.100.7", "x-forwarded-for": "203.0.113.1" }), "198.51.100.7");
  assert.deepEqual(clientIpFromHeaders({ "x-real-ip": "198.51.100.7" }, SOCKET), { ip: "198.51.100.7", isForwarded: true });
});

test("X-Forwarded-For gives its last entry, the address the proxy saw, never one the client wrote", () => {
  assert.equal(ip({ "x-forwarded-for": "198.51.100.7" }), "198.51.100.7");
  assert.equal(ip({ "x-forwarded-for": "203.0.113.66, 198.51.100.7" }), "198.51.100.7");
  assert.equal(ip({ "x-forwarded-for": ["203.0.113.66", "198.51.100.7"] }), "198.51.100.7");
});

test("IPv6 clients in any form (the old check only took the full eight groups)", () => {
  assert.equal(ip({ "x-real-ip": "2001:db8::1" }), "2001:db8::1");
  assert.equal(ip({ "x-forwarded-for": "2001:db8::1" }), "2001:db8::1");
});

test("a header that is not an address does not hide the next one", () => {
  // X-Forwarded-Host is a host name from most proxies: it used to win over X-Forwarded-For and fail.
  assert.equal(ip({ "x-forwarded-host": "prod.openversus.org", "x-forwarded-for": "198.51.100.7" }), "198.51.100.7");
  assert.equal(ip({ "x-real-ip": "unknown", "x-forwarded-for": "198.51.100.7" }), "198.51.100.7");
  assert.equal(ip({ "x-forwarded-host": "198.51.100.7" }), "198.51.100.7");
});

test("without an address in the headers, the connection's own", () => {
  assert.deepEqual(clientIpFromHeaders({}, SOCKET), { ip: SOCKET, isForwarded: false });
  assert.equal(ip({ "x-real-ip": "not-an-ip" }), SOCKET);
  assert.equal(ip({ "x-forwarded-for": "198.51.100.7, garbage" }), SOCKET);
  assert.equal(clientIpFromHeaders({}, undefined).ip, "");
});
