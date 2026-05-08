import * as geoip from "geoip-lite";
import env from "../env/env";

/**
 * Region toolkit for OVS multi-region routing.
 *
 * Adapted from multiversuskoth/mvs-http-server's
 *   apps/mvsi-server/src/modules/matchmaking/matchmaking.matching.ts
 * (340 lines, MIT-licensed). We took the region taxonomy + helpers but
 * dropped their queue-compat / find-matched-groups stuff — our matchmaker
 * (matchmaking-worker.ts) already has its own pairing logic.
 *
 * Two-tier model:
 *
 *   1. PLAYER region — from geo-IP, can be any of the 12 REGIONS below.
 *      Tells us where the player physically is.
 *
 *   2. DEPLOYED region — must be one of `DEPLOYED_REGIONS` (currently
 *      EAST_US + MANCHESTER). The actual rollback server we route the
 *      match to. Cross-region matches snap to one of these via
 *      mapToDeployedRegion() / getMatchServerRegion().
 *
 * To add another rollback host (e.g. WEST_US): add the region slug to
 * DEPLOYED_REGIONS and add the matching env var (UDP_SERVER_IP_WEST_US).
 * No other code changes required.
 */

// ── Regions ──────────────────────────────────────────────────────────

export const REGIONS = [
  "WEST_US",
  "CENTRAL_US",
  "EAST_US",
  "MEXICO_CITY",
  "SAO_PAULO",
  "MANCHESTER", // EU
  "FRANKFURT",  // EU (not deployed; resolves to MANCHESTER)
  "SINGAPORE",
  "TOKYO",
  "MELBOURNE",
] as const;

export type Region = (typeof REGIONS)[number];

/** Regions we actually have rollback hosts for. */
export const DEPLOYED_REGIONS = ["EAST_US", "MANCHESTER"] as const;
export type DeployedRegion = (typeof DEPLOYED_REGIONS)[number];

type RegionInfo = {
  lat: number;
  lon: number;
};

/** Latitude / longitude per region (data-center city). */
export const REGION_COORDS: Record<Region, RegionInfo> = {
  WEST_US: { lat: 34.0522, lon: -118.2437 },   // Los Angeles
  CENTRAL_US: { lat: 32.7767, lon: -96.797 },  // Dallas
  EAST_US: { lat: 39.0438, lon: -77.4874 },    // Ashburn, VA
  MEXICO_CITY: { lat: 19.4326, lon: -99.1332 },
  SAO_PAULO: { lat: -23.5505, lon: -46.6333 },
  MANCHESTER: { lat: 53.4808, lon: -2.2426 },
  FRANKFURT: { lat: 50.1109, lon: 8.6821 },
  SINGAPORE: { lat: 1.3521, lon: 103.8198 },
  TOKYO: { lat: 35.6762, lon: 139.6503 },
  MELBOURNE: { lat: -37.8136, lon: 144.9631 },
};

// ── Distance / closest-region lookup ─────────────────────────────────

/** Haversine distance in km between two lat/lon points. */
export function haversineKm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const toRad = (deg: number) => (deg * Math.PI) / 180;
  const dLat = toRad(lat2 - lat1);
  const dLon = toRad(lon2 - lon1);
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
  return 6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

/** Returns the closest region (of all REGIONS) to the given lat/lon. */
export function getClosestRegion(lat: number, lon: number): Region {
  let closest: Region = REGIONS[0];
  let minDist = Infinity;

  for (const region of REGIONS) {
    const coords = REGION_COORDS[region];
    const dist = haversineKm(lat, lon, coords.lat, coords.lon);
    if (dist < minDist) {
      minDist = dist;
      closest = region;
    }
  }
  return closest;
}

/**
 * Snaps any Region → nearest DeployedRegion (by great-circle distance).
 * Used both for picking which rollback host hosts a match and for
 * defaulting a player's "home server" if their classified region isn't
 * one we deploy to.
 */
export function mapToDeployedRegion(region: Region): DeployedRegion {
  // Fast path: already deployed
  if ((DEPLOYED_REGIONS as readonly string[]).includes(region)) {
    return region as DeployedRegion;
  }
  const coords = REGION_COORDS[region];
  let closest: DeployedRegion = DEPLOYED_REGIONS[0];
  let minDist = Infinity;
  for (const dep of DEPLOYED_REGIONS) {
    const depCoords = REGION_COORDS[dep];
    const d = haversineKm(coords.lat, coords.lon, depCoords.lat, depCoords.lon);
    if (d < minDist) {
      minDist = d;
      closest = dep;
    }
  }
  return closest;
}

// ── Region → rollback host IP ────────────────────────────────────────

/**
 * Returns the public IP of the rollback host for a given deployed region.
 * Falls back to UDP_SERVER_IP if a region-specific env var isn't set —
 * lets single-region deploys keep working without changing config.
 */
export function rollbackHostIpForRegion(region: DeployedRegion): string {
  switch (region) {
    case "EAST_US":
      return env.UDP_SERVER_IP_EAST_US || env.UDP_SERVER_IP;
    case "MANCHESTER":
      return env.UDP_SERVER_IP_MANCHESTER || env.UDP_SERVER_IP;
  }
}

// ── Geo-IP lookup ────────────────────────────────────────────────────

/**
 * Default region used when geo-IP can't classify a player (private IP,
 * lookup miss, missing arg). Defaults to EAST_US since that's our
 * primary host and the bulk of current player population sits in NA.
 */
export const DEFAULT_REGION: Region = "EAST_US";

/**
 * Maps an IP address to its closest Region using the bundled MaxMind
 * GeoLite data shipped with geoip-lite. Falls back to DEFAULT_REGION
 * for private/local IPs, IPv6 we can't resolve, or any lookup miss.
 */
export function regionFromIp(ip: string | null | undefined): Region {
  if (!ip) return DEFAULT_REGION;

  // Strip IPv4-mapped-IPv6 prefix if present.
  const cleaned = ip.replace(/^::ffff:/, "");

  // Skip lookup for obvious local/private addresses (RFC1918 +
  // loopback + link-local). geoip-lite returns null for these but
  // we can short-circuit the work.
  if (
    cleaned === "127.0.0.1" ||
    cleaned === "::1" ||
    cleaned.startsWith("10.") ||
    cleaned.startsWith("192.168.") ||
    cleaned.startsWith("169.254.") ||
    /^172\.(1[6-9]|2\d|3[01])\./.test(cleaned)
  ) {
    return DEFAULT_REGION;
  }

  try {
    const result = geoip.lookup(cleaned);
    if (!result || !Array.isArray(result.ll) || result.ll.length !== 2) {
      return DEFAULT_REGION;
    }
    const [lat, lon] = result.ll;
    return getClosestRegion(lat, lon);
  } catch {
    return DEFAULT_REGION;
  }
}

// ── Region proximity (queue expansion) ───────────────────────────────

type RegionProximityEntry = {
  region: Region;
  /** Milliseconds the ticket must wait in queue before this neighbor is allowed. */
  waitMs: number;
};

/**
 * For each home region, an ordered list of neighbors with the wait
 * time before that neighbor becomes eligible for cross-region matching.
 *
 * Currently only EAST_US and MANCHESTER are neighbors of each other
 * (the only two deployed). Other regions snap to their closest deployed
 * region via mapToDeployedRegion() and inherit its proximity table.
 *
 * Wait timings (per Tugger): EAST_US opens at 7s, MANCHESTER opens at
 * 8s. With only two deployed regions today, opening the neighbor is
 * effectively "open to all" — there's nowhere else to go.
 */
export const REGION_PROXIMITY: Partial<Record<Region, RegionProximityEntry[]>> = {
  EAST_US: [
    { region: "MANCHESTER", waitMs: 7_000 },
  ],
  MANCHESTER: [
    { region: "EAST_US", waitMs: 8_000 },
  ],
};

/**
 * After this elapsed-in-queue time, region restrictions are lifted
 * entirely and the matchmaker will pair across any region. (Pure
 * ELO-based matchmaking takes over at this point.)
 */
export const REGION_INFINITE_AFTER_MS = 15_000;

// ── Match server selection ───────────────────────────────────────────

/**
 * For each cross-region pairing of DEPLOYED regions, the server region
 * that gives the most balanced latency. Key format: "REGION_A:REGION_B"
 * (alphabetically sorted).
 *
 * Two-server world: the only meaningful pair is EAST_US:MANCHESTER, and
 * we send those to EAST_US (Atlantic-cable midpoint is closer to Ashburn
 * than Manchester for typical population balance — flip if EU population
 * grows enough to justify hosting cross-region matches there).
 */
const MATCH_SERVER_OVERRIDES: Record<string, DeployedRegion> = {
  "EAST_US:MANCHESTER": "EAST_US",
};

function pairKey(a: DeployedRegion, b: DeployedRegion): string {
  return a < b ? `${a}:${b}` : `${b}:${a}`;
}

/**
 * Given the regions of the matched players, returns the DEPLOYED server
 * region that should host the match. Snaps any non-deployed player
 * region to its closest deployed region first.
 *
 * - Same deployed region for everyone → that region.
 * - Two distinct deployed regions → MATCH_SERVER_OVERRIDES.
 * - 3+ distinct (impossible today, but keeps the function future-proof)
 *   → most-frequent, tie-break with anchor.
 */
export function getMatchServerRegion(
  anchorRegion: Region,
  ...otherRegions: Region[]
): DeployedRegion {
  const anchorDep = mapToDeployedRegion(anchorRegion);
  const otherDeps = otherRegions.map(mapToDeployedRegion);

  if (otherDeps.every((r) => r === anchorDep)) return anchorDep;

  const unique = [...new Set([anchorDep, ...otherDeps])];
  if (unique.length === 2) {
    const override = MATCH_SERVER_OVERRIDES[pairKey(unique[0], unique[1])];
    if (override) return override;
  }

  // 3+ distinct (future-proofing): pick most frequent, tie-break with anchor.
  const counts = new Map<DeployedRegion, number>();
  for (const r of [anchorDep, ...otherDeps]) {
    counts.set(r, (counts.get(r) ?? 0) + 1);
  }
  let best = anchorDep;
  let bestCount = 0;
  for (const [region, count] of counts) {
    if (count > bestCount) {
      bestCount = count;
      best = region;
    }
  }
  return best;
}

// ── Region expansion (queue compatibility) ───────────────────────────

/**
 * Returns the set of allowed regions for a ticket based on how long
 * it has been in the queue.
 *
 *   - Starts with only the home region.
 *   - Each neighbor unlocks once `elapsedMs` reaches its waitMs threshold.
 *
 * For our current 2-region setup, this means an EAST_US ticket starts
 * matching only with other EAST_US tickets, and after 25s also accepts
 * MANCHESTER. MANCHESTER tickets accept EAST_US after 15s (asymmetric
 * because EU pool will be smaller — surfacing them to NA matches faster
 * keeps EU queue times sane while we ramp population).
 */
export function getAllowedRegions(homeRegion: Region, elapsedMs: number): Set<Region> {
  // Past the global cutoff, region is no longer a filter — return every
  // known region so cross-region matches go through unconstrained.
  if (elapsedMs >= REGION_INFINITE_AFTER_MS) {
    return new Set<Region>(REGIONS);
  }

  const allowed = new Set<Region>([homeRegion]);
  const neighbors = REGION_PROXIMITY[homeRegion];
  if (!neighbors) return allowed;

  for (const entry of neighbors) {
    if (elapsedMs >= entry.waitMs) {
      allowed.add(entry.region);
    }
  }
  return allowed;
}
