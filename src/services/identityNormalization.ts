export function normalizeIdentity(kind: "steam" | "epic" | "install" | "hardware", value: unknown): string {
  if (typeof value !== "string") return "";
  const candidate = value.trim();
  if (!candidate || /^(unknown|null|none|n\/a)$/i.test(candidate)) return "";
  if (kind === "steam") return /^\d{15,20}$/.test(candidate) ? candidate : "";
  if (kind === "epic") return /^[a-f\d]{32}$/i.test(candidate) ? candidate.toLowerCase() : "";
  if (kind === "install") return /^[a-f\d]{32}$/i.test(candidate) ? candidate.toLowerCase() : "";
  return /^[a-f\d]{64}$/i.test(candidate) ? candidate.toLowerCase() : "";
}

export interface HardwareSignal {
  hardwareId: string;
  hardwareIdVersion: string;
  hardwareIdQuality: string;
}

/**
 * V1 hardware hashes can encode generic CPU/"Unknown" inputs and are unsafe.
 * Only explicitly versioned, strong V2 fingerprints are retained as optional
 * enforcement metadata. Hardware never participates in account resolution.
 */
export function normalizeHardwareSignal(id: unknown, version: unknown, quality: unknown): HardwareSignal {
  const hardwareId = normalizeIdentity("hardware", id);
  const hardwareIdVersion = typeof version === "string" ? version.trim() : String(version ?? "").trim();
  const hardwareIdQuality = typeof quality === "string" ? quality.trim().toLowerCase() : "";
  if (!hardwareId || hardwareIdVersion !== "2" || hardwareIdQuality !== "strong") {
    return { hardwareId: "", hardwareIdVersion: "", hardwareIdQuality: "" };
  }
  return { hardwareId, hardwareIdVersion, hardwareIdQuality };
}

export interface LegacyIpCandidate {
  steamId?: unknown;
  epicId?: unknown;
  installId?: unknown;
}

/**
 * Recover an identity-less legacy login only when the IP has one defensible
 * owner. Identity-bearing accounts outrank old identity-less ghosts; multiple
 * identity-bearing accounts mean a shared/contested IP and must never be
 * guessed. If the IP has never gained identity, a sole historical profile is
 * still recoverable for old archive clients.
 */
export function chooseUnambiguousLegacyIpCandidate<T extends LegacyIpCandidate>(candidates: T[]): T | null {
  const canonical = candidates.filter(candidate =>
    !!normalizeIdentity("steam", candidate.steamId) ||
    !!normalizeIdentity("epic", candidate.epicId) ||
    !!normalizeIdentity("install", candidate.installId),
  );
  if (canonical.length === 1) return canonical[0];
  if (canonical.length > 1) return null;
  return candidates.length === 1 ? candidates[0] : null;
}
