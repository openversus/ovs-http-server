import type { IncomingHttpHeaders } from "http";
import { isIP } from "net";

export const REAL_IP_HEADER = "x-real-ip";
export const FORWARDED_FOR_HEADER = "x-forwarded-for";
export const FORWARDED_FOR_HOST_HEADER = "x-forwarded-host";

/**
 * The client's address behind the reverse proxy: the first of X-Real-IP, X-Forwarded-Host and the last entry of
 * X-Forwarded-For that is an IP address (IPv4, or IPv6 in any form), else the connection's own address (a function is
 * called only then).
 *
 * X-Forwarded-For's last entry is the address the proxy saw; the entries before it are whatever the client sent, so
 * they are never used. The headers are trusted as they arrive, so only the reverse proxy should be able to reach the
 * server. Every IP-keyed record (identity:{ip}, connections:{ip}, active_ip_accounts:{ip}, ...) depends on the HTTP
 * routes and the websocket server agreeing on this, which is why both use this function.
 */
export function clientIpFromHeaders(
  headers: IncomingHttpHeaders,
  socketAddress: string | undefined | (() => string | undefined),
): { ip: string; isForwarded: boolean } {
  const header = (name: string) => {
    const value = headers[name];
    return (Array.isArray(value) ? value.join(",") : value ?? "").trim();
  };
  const forwardedFor = header(FORWARDED_FOR_HEADER).split(",");
  const candidates = [header(REAL_IP_HEADER), header(FORWARDED_FOR_HOST_HEADER), forwardedFor[forwardedFor.length - 1].trim()];
  for (const candidate of candidates) {
    if (candidate && isIP(candidate)) return { ip: candidate, isForwarded: true };
  }
  // Asked for only now: reading it can fail (see getRealIP).
  const own = typeof socketAddress === "function" ? socketAddress() : socketAddress;
  return { ip: own ?? "", isForwarded: false };
}
