/** A UDP port a client reported for its rollback node (a number or a numeric string, 1..65535), or 0 for anything else. */
export function parseNodePort(value: unknown): number {
  const n = typeof value === "number" ? value : typeof value === "string" && /^\d{1,5}$/.test(value.trim()) ? Number(value.trim()) : NaN;
  return Number.isInteger(n) && n >= 1 && n <= 65535 ? n : 0;
}

/**
 * Whether a match of these players can run P2P: any mode (1v1, 2v2, FFA, Casual, custom, a set's next game), any number
 * of humans, bots and spectators, as long as one player is a human who plays (not a spectator), since the host's node
 * runs the engine and the config's host is always one. Leaves no match out otherwise.
 */
export function hasP2PHost(players: { isBot?: boolean; isSpectator?: boolean }[]): boolean {
  return players.some((p) => !p.isBot && !p.isSpectator);
}
