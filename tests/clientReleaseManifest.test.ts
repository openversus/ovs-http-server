import assert from "node:assert/strict";
import test from "node:test";
import { buildClientReleaseManifest, flattenClientReleaseManifest } from "../src/services/clientReleaseManifest";

const asset = (name: string, size = 100) => ({
  name,
  size,
  digest: `sha256:${"a".repeat(64)}`,
  browser_download_url: `https://github.com/openversus/ovs-client/releases/download/v1/${name}`,
});

test("builds an individual-file manifest and ignores ZIPs", () => {
  const files = buildClientReleaseManifest([
    asset("OpenVersus_v1.zip"),
    asset("OpenVersus.asi"),
    asset("OVS_P.pak"),
    asset("OVS_P.utoc"),
    asset("OVS_P.ucas"),
  ]);
  assert.deepEqual(files.map(({ name, kind }) => ({ name, kind })), [
    { name: "OVS_P.pak", kind: "paks" },
    { name: "OVS_P.ucas", kind: "paks" },
    { name: "OVS_P.utoc", kind: "paks" },
    { name: "OpenVersus.asi", kind: "plugin" },
  ]);
  assert.equal(flattenClientReleaseManifest(files).file_count, 4);
  assert.equal(flattenClientReleaseManifest(files).file_0_name, "OVS_P.pak");
});

test("rejects an incomplete IoStore group", () => {
  assert.throws(
    () => buildClientReleaseManifest([asset("OpenVersus.asi"), asset("OVS_P.utoc")]),
    /incomplete IoStore group/,
  );
});

test("rejects untrusted URLs and missing digests", () => {
  assert.throws(() => buildClientReleaseManifest([{
    ...asset("OpenVersus.asi"),
    browser_download_url: "https://example.com/OpenVersus.asi",
  }]), /exactly one verified/);
  assert.throws(() => buildClientReleaseManifest([{
    ...asset("OpenVersus.asi"),
    digest: null,
  }]), /exactly one verified/);
});

test("C# releases: the versioned plugin is offered, sidecars and zips are not", () => {
  // Asset names exactly as admin's release pipeline publishes them (2026.09.25.07).
  const files = buildClientReleaseManifest([
    asset("OpenVersus_2026.09.25.07.asi"),
    asset("OpenVersus_2026.09.25.07.asi.sha256"),
    asset("OpenVersus_v2026.09.25.07.zip"),
    asset("OpenVersus_v2026.09.25.07.zip.sha256"),
    asset("SHA256SUMS"),
  ], "2026.09.25.07");
  assert.deepEqual(files.map(({ name, kind }) => ({ name, kind })), [
    { name: "OpenVersus_2026.09.25.07.asi", kind: "plugin" },
  ]);
});

test("a versioned plugin must match the release version; the legacy name is still accepted", () => {
  assert.throws(
    () => buildClientReleaseManifest([asset("OpenVersus_2026.09.25.06.asi")], "2026.09.25.07"),
    /different version/,
  );
  assert.equal(buildClientReleaseManifest([asset("OpenVersus.asi")], "2026.04.08.14")[0].kind, "plugin");
  assert.throws(
    () => buildClientReleaseManifest([asset("OpenVersus.asi"), asset("OpenVersus_2026.09.25.07.asi")], "2026.09.25.07"),
    /exactly one verified/,
  );
});
