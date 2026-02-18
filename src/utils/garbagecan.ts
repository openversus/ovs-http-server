import { logger } from "../config/logger";
import * as nodeutil from "node:util";
import * as AuthUtils from "./auth";
import env from "../env/env";

const BE_VERBOSE: boolean = env.VERBOSE_LOGGING === 0 ? false : true;

export function TryInspectRequestVerbose(req: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  if (BE_VERBOSE) {
    TryInspectRequest(req, depth, showHidden, colors);
  }
}

export function TryInspectRequest(req: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  if (req.ip) {
    logger.info("Request IP: ", req.ip);
  }

  if (req.statusCode) {
    logger.info("Response Status Code: ", req.statusCode);
  }

  if (req.statusMessage) {
    logger.info("Response Status Message: ", req.statusMessage);
  }

  if (req.protocol) {
    logger.info("Protocol: ", req.protocol);
  }

  if (req.headers) {
    logger.info("Headers: ");
    TryInspect(req.headers);
  }

  if (req.body) {
    logger.info("\nBody: ");
    TryInspect(req.body);
  }

  if (req.params) {
    logger.info("\nParams: ");
    TryInspect(req.params);
  }

  if (req.query) {
    logger.info("\nQuery: ");
    TryInspect(req.query);
  }

  if (req.path) {
    logger.info("\nPath: ");
    TryInspect(req.path);
  }

  if (req.token) {
    logger.info("\nToken: ");
    TryInspect(req.token);
  }

  if (req.headers && req.headers["x-hydra-access-token"]) {
    logger.info("\nHydra Acces Token: ");
    try {
      let decodedToken = AuthUtils.DecodeClientToken(req);
      TryInspect(decodedToken);
    }
    catch (error) {
      logger.info("Failed to decode Hydra token:", error);
      logger.info("\nRaw Token:", req.headers["x-hydra-access-token"]);
    }
  }
}

export function TryInspectVerbose(obj: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  if (BE_VERBOSE) {
    TryInspect(obj, depth, showHidden, colors);
  }
}

export function TryInspect(obj: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  try {
    logger.info(nodeutil.inspect(obj, { showHidden: showHidden, depth: depth, colors: colors }));
  }
  catch (error) {
    logger.info("Failed to inspect object:", error);
  }
}

export function TryInspectObjectVerbose(obj: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  if (BE_VERBOSE) {
    TryInspectObject(obj, depth, showHidden, colors);
  }
}

export function TryInspectObject(obj: any, depth: number = 4, showHidden: boolean = false, colors: boolean = true): void {
  try {
    logger.info(nodeutil.inspect(obj, { showHidden: showHidden, depth: depth, colors: colors }));
  }
  catch (error) {
    logger.info("Failed to inspect object:", error);
  }
}

export function isIPv4Address(ip: string): boolean {
  const ipv4Regex =
    /^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$/;
  return ipv4Regex.test(ip);
}

export function isIPv6Address(ip: string): boolean {
  const ipv6Regex = /^(?:[a-fA-F0-9]{1,4}:){7}[a-fA-F0-9]{1,4}$/;
  return ipv6Regex.test(ip);
}

export function isIPAddress(ip: string): boolean {
  return isIPv4Address(ip) || isIPv6Address(ip);
}

export const disabledCharacters: string[] = [
  "character_C022",
  "character_c022",
  "character_supershaggy",
  "character_Meeseeks",
  "character_meeseeks",
  "C022",
  "c022",
  "Supershaggy",
  "supershaggy",
];

export function isDisabledChar(char: string): boolean {
  for (const disabled of disabledCharacters) {
    var charRegex = new RegExp(`\\b${disabled}\\b`, "i");
    if (charRegex.test(char)) {
      return true;
    }
  }
  return false;
}

export function isEnabledChar(char: string): boolean {
  return !isDisabledChar(char);
}
