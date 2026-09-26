import env from "../env/env";

function parts(version: string): number[] | null {
  const clean = version.trim().replace(/^v/i, "").split("-")[0];
  if (!/^\d+(?:\.\d+){0,3}$/.test(clean)) return null;
  return clean.split(".").map(Number);
}

export function compareClientVersions(left: string, right: string): number {
  const a = parts(left);
  const b = parts(right);
  if (!a || !b) return 0;
  const length = Math.max(a.length, b.length);
  for (let i = 0; i < length; i++) {
    const delta = (a[i] || 0) - (b[i] || 0);
    if (delta !== 0) return delta < 0 ? -1 : 1;
  }
  return 0;
}

export function isClientUpdateRequired(version: string): boolean {
  if (!env.CLIENT_VERSION_CHECK) return false;
  return isClientUpdateRequiredForMinimum(version, env.MIN_CLIENT_VERSION);
}

export function isClientUpdateRequiredForMinimum(version: string, configuredMinimum: string): boolean {
  const minimum = configuredMinimum.trim();
  if (!minimum) return false;
  if (!parts(minimum)) return false;
  // Once a minimum is activated, clients that do not identify their version
  // are legacy by definition and must update.
  if (!parts(version)) return true;
  return compareClientVersions(version, minimum) < 0;
}

/**
 * Gameplay additionally requires proof that this session was bootstrapped by
 * /api/identify. This remains enforced even while the version minimum is
 * disabled for development or staged rollout testing.
 */
export function isClientGameplayAccessRequiredForMinimum(
  version: string,
  configuredMinimum: string,
  identityRegistered: boolean,
  checkEnabled: boolean = env.CLIENT_VERSION_CHECK,
): boolean {
  if (!checkEnabled) return false;
  if (!identityRegistered) return true;
  return isClientUpdateRequiredForMinimum(version, configuredMinimum);
}
