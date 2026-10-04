/** A UDP port a client reported for its rollback node (a number or a numeric string, 1..65535), or 0 for anything else. */
export function parseNodePort(value: unknown): number {
  const n = typeof value === "number" ? value : typeof value === "string" && /^\d{1,5}$/.test(value.trim()) ? Number(value.trim()) : NaN;
  return Number.isInteger(n) && n >= 1 && n <= 65535 ? n : 0;
}
