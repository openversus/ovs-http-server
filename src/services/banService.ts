import { logger } from "../config/logger";
import { readFileSync, appendFileSync } from "fs";
import { join } from "path";
import env from "../env/env";

const serviceName = "Services.BanService";
const logPrefix = `[${serviceName}]:`;

const IPBansFile: string = env.IP_BANS_FILE || "../data/bans.txt";
const CIDRBansFile: string = env.CIDR_BANS_FILE || "../data/cidr_bans.txt";
const BannedNamesFile: string = env.BANNED_NAMES_FILE || "../data/banned_names.txt";
const ForceChangeNamesFile: string = env.FORCE_CHANGE_NAMES_FILE || "../data/force_change_names.txt";

function syncReadFile(filename: string = IPBansFile): string[] {
  var bans: string = "";
  logger.info(`${logPrefix} Reading bans from ${filename}...`);

  try {
    bans = readFileSync(join(__dirname, filename), "utf-8");
  }
  catch (error) {
    logger.error(`${logPrefix} Error reading bans from ${filename}:`, error);
    return [];
  }

  const contents = bans;
  const arr = contents.split(/\r?\n/);

  // Remove empty lines and comments
  for (let i = arr.length - 1; i >= 0; i--) {
    if (undefined === arr[i]) {
      continue;
    }
    if (null === arr[i] || arr[i].trim() === "") {
      arr.splice(i, 1);
      continue;
    }
    //if (arr[i] && undefined !== arr[i] && null !== arr[i] && typeof arr[i] === 'string' && arr[i].startsWith("#")) {
    if (typeof arr[i] === "string" && arr[i].startsWith("#")) {
      arr.splice(i, 1);
      continue;
    }
  }

  logger.info(`${logPrefix} Bans file loaded. Total bans from ${filename}: ${arr.length}`);

  return arr;
}

function syncWriteFile(filename: string, data: string | Array<string>): void {
  try {
    const filePath = join(__dirname, filename);
    readFileSync(filePath, "utf-8"); // Make sure the file exists
    const updatedData = Array.isArray(data) ? data.join("\n") + "\n" : data + "\n";
    appendFileSync(filePath, updatedData, "utf-8");
    logger.info(`${logPrefix} Successfully wrote to ${filename}`);
  }
  catch (error) {
    logger.error(`${logPrefix} Error writing to ${filename}, error: ${JSON.stringify(error)}`);
  }
}

export const cidrBans = syncReadFile(CIDRBansFile);
export const bannedNames = syncReadFile(BannedNamesFile);
export const forceChangeNames = syncReadFile(ForceChangeNamesFile);

export function getBans(): string[] {
  return syncReadFile();
}

export function getBannedNames(): string[] {
  return bannedNames.map((name) => name.toLowerCase());
}

export function getForceChangeNames(): string[] {
  return forceChangeNames.map((name) => name.toLowerCase());
}

export function getCIDRBans(): string[] {
  if (undefined === cidrBans || null === cidrBans || cidrBans.length === 0) {
    var returnObject: string[] = [];
    returnObject = syncReadFile(CIDRBansFile);
    return returnObject;
  }
  return cidrBans;
}

export function isNameBanned(name: string): boolean {
  const bannedNames = getBannedNames();
  return bannedNames.includes(name.toLowerCase());
}

export function isNameForceChange(name: string): boolean {
  const forceChangeNames = getForceChangeNames();
  return forceChangeNames.includes(name.toLowerCase());
}

export function stringContainsBannedName(string: string): boolean {
  var bannedPatternString: string = getBannedNames().join("|");
  var bannedPattern = new RegExp(`\\b(${bannedPatternString})\\b`, "i");
  return bannedPattern.test(string);
}

export function stringContainsForceChangeName(string: string): boolean {
  var forceChangePatternString: string = getForceChangeNames().join("|");
  var forceChangePattern = new RegExp(`\\b(${forceChangePatternString})\\b`, "i");
  return forceChangePattern.test(string);
}

export function isBanned(ipAddress: string): boolean {
  const bans = getBans();
  const cidrBans = getCIDRBans();
  return bans.includes(ipAddress) || cidrBans.some((cidr) => isIPInCIDRBlock(ipAddress, cidr));
}

export function isCIDRBanned(ipAddress: string): boolean {
  const cidrBans = getCIDRBans();
  return cidrBans.some((cidr) => isIPInCIDRBlock(ipAddress, cidr));
}

export function isIPInCIDRBlock(ipAddress: string, cidrBlock: string): boolean {
  const [
    block,
    prefixLength,
  ] = cidrBlock.split("/");
  const blockParts = block.split(".").map(Number);
  const ipParts = ipAddress.split(".").map(Number);
  const mask = ~(2 ** (32 - Number(prefixLength)) - 1);

  const blockInt = (blockParts[0] << 24) | (blockParts[1] << 16) | (blockParts[2] << 8) | blockParts[3];
  const ipInt = (ipParts[0] << 24) | (ipParts[1] << 16) | (ipParts[2] << 8) | ipParts[3];

  return (blockInt & mask) === (ipInt & mask);
}

export function banIP(
  ipAddress: string,
  playerID: string = "Not Provided",
  oldName: string = "Not Provided",
  newName: string = "Not Provided",
  reason?: string,
): void {
  const banEntry: string[] = [];
  const randomUUID: string = crypto.randomUUID();
  const banCommentLine: string = `# ${new Date().toISOString()} - Ban ID: ${randomUUID}, IP: ${ipAddress}, Player ID: ${playerID}, Old Name: ${oldName}, New Name: ${newName}, Reason: ${reason || "No reason provided"}`;
  banEntry.push(banCommentLine);
  banEntry.push(ipAddress);
  const bans = getBans();
  if (!bans.includes(ipAddress)) {
    syncWriteFile(IPBansFile, banEntry);
    logger.info(`${logPrefix} IP Address ${ipAddress} has been banned and written to IP Bans File: ${IPBansFile}`);
  }
  else {
    logger.info(`${logPrefix} IP Address ${ipAddress} is already in the bans list.`);
  }
}

export function GetBanWarningMessage(ipAddress: string): string {
  return `[${serviceName}]: A connection attempt from banned IP Address ${ipAddress} was denied.`;
}
