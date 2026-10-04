// P2P rollback: which matches run on the players' own nodes instead of a rollback server.
//
// A node (OVS.Rollback.Node) on each player's machine is the rollback server the game connects to, at
// 127.0.0.1:P2P_NODE_PORT. The match config's host (is_host) runs the engine inside its node; every other node
// forwards its game to the host over a hole-punched path, and all of them fall back to the relay (the on-demand
// rollback server, when P2P_DEPLOY_RELAY=1) if no path opens. The node fetches the config with /ovs_register,
// posts /ovs_p2p_ready when it serves (which is when the players get game-server-instance-ready) and
// /ovs_match_started when the match starts.
import env from "./env/env";
import { redisClient } from "./config/redis";
import type { MATCH_FOUND_NOTIFICATION, RedisTeamEntry } from "./config/redis";

import { parseNodePort } from "./services/nodePort";

export { parseNodePort };

/**
 * The port this player's game is sent to for a P2P match: the one its client reported for its node
 * (/api/identify, kept in connections:<id> by /access and by a late identify), else P2P_NODE_PORT, the
 * fixed port a node takes when it can.
 */
export async function nodePortFor(playerId: string): Promise<number> {
  try {
    return parseNodePort(await redisClient.hGet(`connections:${playerId}`, "nodePort")) || env.P2P_NODE_PORT;
  } catch {
    return env.P2P_NODE_PORT;
  }
}

/** Only 1v1 between two humans for now: a host whose connection drops ends the match for everyone, and the
 *  relay would have handed the slot to AI; team modes wait for that. */
export function isP2PEligible(players: RedisTeamEntry[]): boolean {
  if (env.P2P_ROLLBACK !== 1) return false;
  const humans = players.filter((p) => !p.isBot && !p.isSpectator);
  const spectators = players.filter((p) => p.isSpectator);
  return humans.length === 2 && spectators.length === 0;
}

/** Marks the notification (which becomes the stored match config) as a P2P match when eligible. */
export function markP2P(notification: MATCH_FOUND_NOTIFICATION): boolean {
  notification.p2p = isP2PEligible(notification.players);
  return notification.p2p;
}

/** Whether the on-demand rollback server is deployed when the match is created: only for a server match. A P2P
 *  match gets one only if its nodes report that no direct path opened (/ovs_p2p_failed), as their relay. */
export function deploysRollbackServer(p2p: boolean): boolean {
  return !p2p;
}

/** Redis key set once the relay for a P2P match has been requested (and deployed, when on-demand is on). */
export const p2pRelayKey = (matchId: string) => `p2p_relay:${matchId}`;
