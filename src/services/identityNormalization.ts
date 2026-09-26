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
  provisional?: unknown;
}

/** True when the account carries a Steam, Epic or install id. */
export function hasDurableIdentity(candidate: LegacyIpCandidate): boolean {
  return !!normalizeIdentity("steam", candidate.steamId)
    || !!normalizeIdentity("epic", candidate.epicId)
    || !!normalizeIdentity("install", candidate.installId);
}

/** Mongo clauses matching an account that carries a durable id (see hasDurableIdentity). */
const DURABLE_ID_CLAUSES = [
  { steamId: /^\d{15,20}$/ },
  { epicId: /^[a-f\d]{32}$/i },
  { installId: /^[a-f\d]{32}$/i },
];

/** Mongo filter for the accounts on this IP that have no durable id. */
export function idLessAccountFilter(ip: string, provisional: boolean) {
  return {
    ip,
    provisional: provisional ? true : { $ne: true },
    $nor: DURABLE_ID_CLAUSES,
  };
}

/**
 * Install-id adoption. A client that now sends a durable id
 * (typically the first launch of the C# client) but matches no account may be the
 * owner of an older account on this IP that was created before it had any id
 * (e.g. the Internet Archive build, which has no Steam id). Adopt only:
 *  1. the IP's single non-provisional account with no durable id, else
 *  2. the IP's most recently seen provisional account (that device's pre-update login).
 * Accounts that already carry a durable id are never taken, and two or more
 * id-less accounts (a household) are ambiguous: that falls to manual merging.
 */
export function chooseAdoptionCandidate<T extends LegacyIpCandidate & { lastSeenAt?: Date | string | null }>(
  candidates: T[],
): T | null {
  const idLess = candidates.filter((candidate) => !hasDurableIdentity(candidate));
  const legacy = idLess.filter((candidate) => candidate.provisional !== true);
  if (legacy.length === 1) return legacy[0];
  if (legacy.length > 1) return null;
  const provisional = idLess
    .filter((candidate) => candidate.provisional === true)
    .sort((a, b) => new Date(b.lastSeenAt ?? 0).getTime() - new Date(a.lastSeenAt ?? 0).getTime());
  return provisional[0] ?? null;
}

/**
 * Recover an identity-less legacy login only when the IP has one defensible
 * owner. Identity-bearing accounts outrank old identity-less ghosts; multiple
 * identity-bearing accounts mean a shared/contested IP and must never be
 * guessed. If the IP has never gained identity, a sole historical profile is
 * still recoverable for old archive clients.
 */
export function chooseUnambiguousLegacyIpCandidate<T extends LegacyIpCandidate>(candidates: T[]): T | null {
  // Provisional accounts (see handlers/access.ts) never own an IP.
  const real = candidates.filter((candidate) => candidate.provisional !== true);
  const canonical = real.filter(hasDurableIdentity);
  if (canonical.length === 1) return canonical[0];
  if (canonical.length > 1) return null;
  return real.length === 1 ? real[0] : null;
}
