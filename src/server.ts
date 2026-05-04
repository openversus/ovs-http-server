import { logger, logwrapper, BE_VERBOSE } from "./config/logger";
import express from "express";
import cookieParser from "cookie-parser";
import router from "./router";
import { hydraDecoderMiddleware } from "./middleware/hydraParser";
import * as https from "https";
import * as http from "http";
import * as fs from "fs";
import * as path from "path";
import { hydraTokenMiddleware, SECRET } from "./middleware/auth";
import { connect } from "./database/client";
import { generate_hiss } from "./handlers/hiss_amalgation_get";
import { redisClient,
  redisGetMatchConfig,
  redisPublisdEndOfMatch,
  redisGetLobbyState,
  redisSaveLobbyState,
  RedisPlayerConnection,
  redisGetPlayerConnectionByIP,
  redisSavePlayerLobby,
  redisPublishLobbyRejoin,
  RedisLobbyRejoinNotification,
  redisSavePartyKey,
  redisGetPartyKey,
  redisDeletePartyKey,
  redisGetPlayerLobby,
  redisGameServerInstanceReady,
  redisSetPendingJoinLobby,
  redisSaveIdentity,
  redisPopDLLNotifications,
  redisPushDLLNotification,
  redisGetOnlinePlayers,
  redisGetActiveRankedSets,
  redisGetInProgressMatches } from "./config/redis";
import { getLeaderboard, getPlayerRank, processMatchLeave, eloToTierDivision } from "./services/eloService";
import { performGenuineLeave } from "./ssc/ssc";
import {
  createLobby,
  joinLobby,
  leaveLobby,
  switchTeam,
  toggleReady,
  setMapPool,
  selectMode,
  startMatch,
  getLobbyWithStatus,
  moveToSpectator,
  moveToPlayer,
  kickPlayer,
} from "./services/customLobbyService";
import { getMapList } from "./data/maps";
import { GAME_SERVER_PORT } from "./game/udp";
import { sscRouter } from "./ssc/routes";
import { getCurrentCRC, LoadConfig, MATCHMAKING_CRC } from "./data/config";
import { PlayerTester, PlayerTesterModel } from "./database/PlayerTester";
import { Types } from "mongoose";
import { RegExpMatcher, TextCensor, englishDataset, englishRecommendedTransformers, asteriskCensorStrategy } from "obscenity";
import env from "./env/env";
import { syncRouter } from "./dataAssetSync";
import { loadAssets } from "./loadAssets";
import { randomInt, createHmac, randomBytes } from "crypto";
import * as SharedTypes from "./types/shared-types";
import { isParameter } from "typescript";
import * as nodeutil from "node:util";
import * as KitchenSink from "./utils/garbagecan";
import * as AuthUtils from "./utils/auth";
import { AccountToken, IAccountToken } from "./types/AccountToken";
import { isNameBanned, isNameForceChange, stringContainsBannedName, stringContainsForceChangeName, banIP } from "./services/banService";
import { NameGenerator } from "./utils/namegeneration";
import { handleDeployRollbackServer, handleDestroyRollbackServer } from "./handlers/testing";
import { handleMatchStatusUpdate } from "./handlers/match_status";
import { resolveAccountFromRequest, resolveAccountWithSource } from "./services/identityService";
import { initAccelByteLobbyWs, accelByteLobbyWs } from "./accelByteLobbyWs";
import { IMatchStatus } from "./interfaces/IMatchStatus";
import { REAL_IP_HEADER, getRealIP, tryGetRealIP } from "./middleware/auth";

// HTML Rendering
const handlebars = require("handlebars");

const serviceName: string = "Server";
const logPrefix = `[${serviceName}]:`;

const matcher = new RegExpMatcher({
  ...englishDataset.build(),
  ...englishRecommendedTransformers,
});
const censor = new TextCensor();
censor.setStrategy(asteriskCensorStrategy());

const defaultPlayer: PlayerTester = new PlayerTesterModel({ ip: "224.0.0.254", name: "Default Player" });
const defaultPlayerToken: AccountToken = new AccountToken() as IAccountToken;

export const app = express();
app.disable("x-powered-by");

const port = env.HTTP_PORT || 8000;
const USE_INTERNAL_ROLLBACK = env.USE_INTERNAL_ROLLBACK === 1 ? true : false;
const USE_INTERNAL_ROLLBACK_CPP = env.USE_INTERNAL_ROLLBACK_CPP === 1 ? true : false;
const MATCH_UPDATE_KEY = env.MATCHUPDATEKEY || "MisconfiguredMatchUpdateKey";
const stringIsOnlyWhitespace = (string: string): boolean => string.trim().length === 0;

process.on("warning", (e) => {
  logger.warn(`${logPrefix} ${e.stack}`);
});

app.use(express.json());
app.use(express.urlencoded());
app.use(cookieParser());

// Leaderboard endpoint — BEFORE Hydra middleware, manual encoding
import { HydraEncoder } from "mvs-dump";
app.get("/leaderboards/:slug/show", async (req, res) => {
  const slug = req.params.slug;
  const mode = slug.includes("1v1") ? "1v1" : slug.includes("2v2") ? "2v2" : "1v1";
  logger.info(`${logPrefix} [PRE-HYDRA] GET /leaderboards/${slug}/show`);

  try {
    const leaderboard = await getLeaderboard(mode as "1v1" | "2v2", 100);
    // Each entry = full Hydra account profile (same as accounts/wb_network/bulk) + rank/score
    const entries = leaderboard.map((entry) => ({
      updated_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
      created_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
      deleted: false,
      orphaned: false,
      orphaned_reason: null,
      public_id: entry.account_id,
      "identity.avatar": "",
      "identity.default_username": true,
      "identity.alternate.wb_network": [{ id: entry.account_id, username: entry.username, avatar: null }],
      "identity.alternate.steam": [{ id: entry.account_id, username: entry.username, avatar: null }],
      "wb_account.completed": true,
      "wb_account.email_verified": true,
      points: 0,
      state: "normal",
      wbplay_data_synced: false,
      wbplay_identity: null,
      locale: "en-US",
      "data.LastLoginPlatform": "EPlatform::PC",
      "data.__unused": null,
      "server_data.ProfileIcon.Slug": "profile_icon_default",
      "server_data.ProfileIcon.AssetPath": "/Game/Panda_Main/Blueprints/Rewards/ProfileIcons/ProfileIcon_Default.ProfileIcon_Default",
      "server_data.CurrentXP": 100,
      "server_data.Level": 5,
      id: entry.account_id,
      "identity.username": entry.username,
      connections: [],
      presence_state: 1,
      presence: "offline",
      rank: entry.rank,
      score: entry.elo,
    }));

    // Manually Hydra-encode and send
    const encoder = new HydraEncoder();
    encoder.encodeValue(entries);
    const encoded = encoder.returnValue();

    logger.info(`${logPrefix} [PRE-HYDRA] Sending ${entries.length} entries, ${encoded.length} bytes`);
    res.setHeader("Content-Type", "application/x-ag-binary");
    res.setHeader("X-Hydra-Server-Time", (Date.now() / 1000).toString());
    res.end(encoded);
  } catch (e) {
    logger.error(`${logPrefix} [PRE-HYDRA] Error: ${e}`);
    const encoder = new HydraEncoder();
    encoder.encodeValue([]);
    res.setHeader("Content-Type", "application/x-ag-binary");
    res.end(encoder.returnValue());
  }
});
app.get("/leaderboards/:slug/around/:playerId", async (req, res) => {
  const slug = req.params.slug;
  const playerId = req.params.playerId;
  const mode = slug.includes("1v1") ? "1v1" : slug.includes("2v2") ? "2v2" : "1v1";
  logger.info(`${logPrefix} [PRE-HYDRA] GET /leaderboards/${slug}/around/${playerId}`);

  try {
    const playerRank = await getPlayerRank(playerId, mode as "1v1" | "2v2");
    const encoder = new HydraEncoder();

    if (playerRank) {
      // Return the player's own entry
      encoder.encodeValue([{
        identity: playerId,
        rank: playerRank.rank,
        score: playerRank.elo,
      }]);
    } else {
      encoder.encodeValue([]);
    }

    res.setHeader("Content-Type", "application/x-ag-binary");
    res.setHeader("X-Hydra-Server-Time", (Date.now() / 1000).toString());
    res.end(encoder.returnValue());
  } catch (e) {
    logger.error(`${logPrefix} [PRE-HYDRA] around error: ${e}`);
    const encoder = new HydraEncoder();
    encoder.encodeValue([]);
    res.setHeader("Content-Type", "application/x-ag-binary");
    res.end(encoder.returnValue());
  }
});
app.get("/global_configuration_types/eula/global_configurations/*", (req, res, next) => {
  res.json(200);
});

app.use(syncRouter);

// HTML File Setup
const filePath = path.join(__dirname, "static/name_change.html");
const source = fs.readFileSync(filePath, "utf8");
const template = handlebars.compile(source);

const partyFilePath = path.join(__dirname, "static/party.html");
const partySource = fs.readFileSync(partyFilePath, "utf8");
const partyTemplate = handlebars.compile(partySource);

const leaderboardFilePath = path.join(__dirname, "static/leaderboard.html");
const leaderboardSource = fs.readFileSync(leaderboardFilePath, "utf8");
const leaderboardTemplate = handlebars.compile(leaderboardSource);

const matchesFilePath = path.join(__dirname, "static/matches.html");
const matchesSource = fs.readFileSync(matchesFilePath, "utf8");
const matchesTemplate = handlebars.compile(matchesSource);

const customLobbyFilePath = path.join(__dirname, "static/custom_lobby.html");
const customLobbySource = fs.readFileSync(customLobbyFilePath, "utf8");
const customLobbyTemplate = handlebars.compile(customLobbySource);

const accountPickerFilePath = path.join(__dirname, "static/account_picker.html");
const accountPickerSource = fs.readFileSync(accountPickerFilePath, "utf8");
const accountPickerTemplate = handlebars.compile(accountPickerSource);

const accountVerifyFilePath = path.join(__dirname, "static/account_verify.html");
const accountVerifySource = fs.readFileSync(accountVerifyFilePath, "utf8");
const accountVerifyTemplate = handlebars.compile(accountVerifySource);

const homeFilePath = path.join(__dirname, "static/home.html");
const homeSource = fs.readFileSync(homeFilePath, "utf8");
const homeTemplate = handlebars.compile(homeSource);

const adminBannerFilePath = path.join(__dirname, "static/admin.html");
const adminBannerSource = fs.readFileSync(adminBannerFilePath, "utf8");
const adminBannerTemplate = handlebars.compile(adminBannerSource);

app.get("/home", async (req, res) => {
  try {
    const html = homeTemplate({});
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /home: ${e}`);
    res.status(500).send("Error loading home page");
  }
});

app.get("/namechange", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    if (typeof ip !== "string") {
      res.status(400).send("Invalid IP");
      return;
    }
    const { player: resolvedPlayer, pickerShown } = await resolvePlayerForWeb(req, res, "/namechange");
    if (pickerShown) return;
    let player = resolvedPlayer;
    // If no player exists, create a new document with empty name
    if (!player) {
      var randomName = NameGenerator.NewName();
      player = new PlayerTesterModel({ ip, name: randomName });
      logger.info(`${logPrefix} No player found for IP ${ip}. Creating new player with name "${randomName}" for IP ${ip}.`);
      await player.save();
    }

    logger.info(`${logPrefix} Name change requested for IP ${ip} with current name "${player.name}"`);
    logwrapper.verbose(`${logPrefix} Name change Player document before: ${JSON.stringify(player)}`);

    // HTML Replacement by brettlyc
    const html = template({
      currentUsername: player.name,
      error: null,
      success: null
    });

    res.send(html);
  }
  catch (e) {
    logger.error(`${logPrefix} Error in route /namechange GET: ${e}`);
    res.send("");
  }
});

// POST /namechange - update or create player by IP
app.post("/namechange", async (req, res, next) => {
  if (!req.body) {
    res.status(400).send("Name is required");
    return;
  }
  try {
    let error = null;
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const { player: resolvedPlayer, pickerShown } = await resolvePlayerForWeb(req, res, "/namechange");
    if (pickerShown) return;
    let player = resolvedPlayer;
    if (!player) {
      logger.warn(
        `${logPrefix} No player found for IP ${ip} during name change POST. This should not happen since the GET route creates a player if one doesn't exist.`,
      );
    }
    logwrapper.verbose(`${logPrefix} ${JSON.stringify(req.body)}`);
    let { name } = req.body;
    if (typeof ip !== "string" || typeof name !== "string") {
      res.status(400).send("Invalid IP or name format");
      return;
    }

    if (name.length === 0 || stringIsOnlyWhitespace(name)) {
        error = "Blank or whitespace-only names are not permitted.";
    }

    if (stringContainsBannedName(name)) {
        error = `The name ${name} contains racial slurs, hate speech, or another banned term which is not welcome in the OVS community. You are now permanently banned from participating in matches held on OVS servers. If you think this is an error, please join the OVS Discord server at https://discord.gg/ez3Ve7eTvk and ping one of the admins.`;
        banIP(
        ip,
        player ? player.profile_id.toString() : "Unknown Player ID",
        player ? player.name : "Unknown Old Name",
        name,
        "Banned name used",
      );
    }

    if (stringContainsForceChangeName(name)) {
      error = `The name ${name} contains a term which is not permitted in a player name. Your name has not been changed. Please refresh the page and choose a new name. If you think this is an error, please join the OVS Discord server at https://discord.gg/ez3Ve7eTvk and ping one of the admins.`;
    }

    if (name.length > 24) {
      name = name.substring(0, 24);
    }

    // Check for unique name (case-insensitive)
    if (!error) {
      const existing = await PlayerTesterModel.findOne({
        name: { $regex: `^${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`, $options: "i" },
      });
      if (existing && existing.ip !== ip) {
        error = `The name "${name}" is already taken by another player. Please choose a different name.`;
      }
    }

    // Update the player's name by account id (household-safe). Falls back to IP-based
    // upsert only if we couldn't resolve an account — preserves old behavior for fully
    // unauthenticated admin access.
    let filtered;
    if(!error) {
      const matches = matcher.getAllMatches(name);
      filtered = censor.applyTo(name, matches);
      const trimmed = filtered.substring(0, 24).trim();
      if (player?.id) {
        await PlayerTesterModel.findOneAndUpdate(
          { _id: new Types.ObjectId(player.id) },
          { name: trimmed },
          { new: true },
        );
      } else {
        await PlayerTesterModel.findOneAndUpdate({ ip }, { name: trimmed }, { upsert: true, new: true });
      }

      // Also update the live Redis connection hash so the live matches page,
      // notification routing, and any in-flight handlers see the new name
      // immediately — without waiting for the player to reconnect via /access.
      // We only update the username field (partial hSet) so other JWT claims
      // already in the hash aren't disturbed.
      try {
        if (player?.id) {
          await redisClient.hSet(`connections:${player.id}`, { username: trimmed });
          // Also keep the IP-keyed legacy mirror in sync if it exists.
          if (ip) {
            const ipHashExists = await redisClient.exists(`connections:${ip}`);
            if (ipHashExists) {
              await redisClient.hSet(`connections:${ip}`, { username: trimmed });
            }
          }
        }
      } catch (redisErr) {
        logger.error(`${logPrefix} Name change: Mongo updated but Redis sync failed for ${player?.id}: ${redisErr}`);
        // Don't fail the request — Mongo is source of truth and the next
        // /access will re-sync Redis from it.
      }
    }

    // HTML Replacement by brettlyc
    const html = template({
      currentUsername: error ? player?.name : name,
      error: error,
      success: !error
    });

    res.send(html);

    logger.info(`[${serviceName}]: Name change for IP ${ip} to "${name}" (filtered: "${filtered}")`);
    if (player) {
      logwrapper.verbose(`${logPrefix} Name change Player document after update: ${JSON.stringify(player)}`);
    }
  }
  catch (e) {
    logger.error(`${logPrefix} Error in route /namechange POST: ${e}`);
    res.send("");
  }
});

app.post("/ovs_register", async (req, res, next) => {
  logger.info(`${logPrefix} OVS GET REGISTRY called`);

  if (!req.body)
  {
    logger.info(`${logPrefix} Invalid OVS GET REGISTRY call with missing body`);
    res.send("");
    return;
  }

  const body = req.body;
  if (!body.matchId || !body.key) {
    logger.info(`${logPrefix} Invalid OVS GET REGISTRY call with missing matchId or key`);
    res.send("");
    return;
  }
  logwrapper.verbose(`${logPrefix} OVS GET REGISTRY body: ${JSON.stringify(body)}`);

  let rollbackHostname: string = "Unknown";
  if (body.hostname)
  {
    rollbackHostname = body.hostname;
  }
  logger.info(`${logPrefix} OVS GET REGISTRY call for MatchID: ${body.matchId} from rollback server: ${rollbackHostname}`);

  const config = await redisGetMatchConfig(body.matchId);
  if (!config || !config.matchKey || config.matchKey !== body.key) {
    logger.info(`${logPrefix} Invalid OVS GET REGISTRY call with invalid match config or key mismatch`);
    res.send("");
    return;
  }
  // Send all players (including spectators, excluding bots) to the rollback server.
  // Bots are DLL/game-side fictions — they have no client to make a UDP connection,
  // so the rollback must NOT wait for them. The game spawns bots locally based on the
  // separately-delivered gameplay config; the rollback only orchestrates real UDP clients.
  const realPlayers = config.players.filter((p) => !p.isBot);
  const players = await Promise.all(realPlayers.map(async (p) => {
    // Prefer ID-keyed connection (stable across NAT/VPN); fall back to IP-keyed for legacy records
    let conn: any = p.playerId
      ? await redisClient.hGetAll(`connections:${p.playerId}`).catch(() => null)
      : null;
    if (!conn || !conn.id) {
      conn = await redisGetPlayerConnectionByIP(p.ip).catch(() => null);
    }
    return {
      player_index: p.playerIndex,
      player_id: p.playerId,
      player_name: conn?.username || "Unknown",
      player_character: conn?.character || "Unknown",
      ip: p.ip,
      is_host: p.isHost,
      is_spectator: p.isSpectator ?? false,
    };
  }));
  const botCount = config.players.length - realPlayers.length;
  if (botCount > 0) {
    logger.info(`${logPrefix} Match ${body.matchId} has ${botCount} bot(s) — excluded from rollback registration. max_players=${realPlayers.length}`);
  }
  res.json({
    max_players: realPlayers.length,
    match_duration: 36000,
    players,
  });

  // Now that the rollback server has the match config, tell ALL real-player clients
  // (including spectators, excluding bots) to connect. Bots have no client to notify.
  const playerIds = realPlayers.map((p) => p.playerId);
  await redisGameServerInstanceReady(body.matchId, playerIds);
  logger.info(`${logPrefix} Sent game-server-instance-ready for match ${body.matchId} after rollback server fetched config`);
});

// Called by the rollback server when gameplay actually begins (first frame).
// Used to suppress match_cancel DLL notifications for mid-game disconnects —
// those should let the game play out rather than yanking the remaining player to the menu.
app.post("/ovs_match_started", async (req, res, next) => {
  if (!req.body?.matchId || !req.body?.key) {
    res.send("");
    return;
  }
  const body = req.body;
  const config = await redisGetMatchConfig(body.matchId);
  if (!config || !config.matchKey || config.matchKey !== body.key) {
    res.send("");
    return;
  }
  await redisClient.set(`match_started:${body.matchId}`, "1", { EX: 600 });
  logger.info(`${logPrefix} OVS MATCH STARTED for MatchID: ${body.matchId} — match_cancel DLL suppressed for mid-game disconnects`);
  res.send("");
});

app.post("/ovs_end_match", async (req, res, next) => {
  logger.info(`${logPrefix} OVS END MATCH call from rollback server`);
  if (!req.body)
  {
    logger.info(`${logPrefix} Invalid OVS END MATCH call with missing body`);
    res.send("");
    return;
  }

  const body = req.body;
  if (!body.matchId || !body.key) {
    logger.info(`${logPrefix} Invalid OVS END MATCH call with missing matchId or key`);
    res.send("");
    return;
  }
  logwrapper.verbose(`${logPrefix} OVS END MATCH body: ${JSON.stringify(body)}`);

  let rollbackHostname: string = "Unknown";
  if (body.hostname)
  {
    rollbackHostname = body.hostname;
  }
  logger.info(`${logPrefix} OVS END MATCH call for MatchID: ${body.matchId} from rollback server: ${rollbackHostname}`);

  const config = await redisGetMatchConfig(body.matchId);
  if (!config || !config.matchKey || config.matchKey !== body.key) {
    res.send("");
    return;
  }

  logwrapper.verbose(`${logPrefix} OVS END MATCH body: ${JSON.stringify(body)}`);
  if (config) {
    await redisPublisdEndOfMatch(
      config.players.map((p) => p.playerId),
      config.matchId,
    );

    // ELO is now processed via client-submitted submit_end_of_match_stats (WinningTeamIndex),
    // not from the rollback server. This allows ephemeral rollback servers (C# rewrite)
    // that don't need to report match results.

    res.send("");
  }
});

// ── Rollback Server Match Status Events ──
// Receives status updates from the UDP rollback server (heartbeat, player connect/disconnect,
// match start/end, errors, etc.).
app.post(["/ovs_match_status", "/api/ovs_match_status"], async (req, res) => {
  // Validate MatchUpdateKey header (auth from rollback server)
  const matchUpdateKey = req.header("MatchUpdateKey") || "";
  if (matchUpdateKey && matchUpdateKey.toLowerCase() !== MATCH_UPDATE_KEY.toLowerCase()) {
    logger.warn(`${logPrefix} POST /api/ovs_match_status invalid MatchUpdateKey — got "${matchUpdateKey}" expected "${MATCH_UPDATE_KEY}"`);
    res.status(403).json({ error: "Invalid signature" });
    return;
  }

  if (!req.body) {
    logger.warn(`${logPrefix} POST /api/ovs_match_status missing body`);
    res.status(400).json({ error: "Missing body" });
    return;
  }

  const ok = await handleMatchStatusUpdate(req, res).catch((e: unknown) => {
    logger.error(`${logPrefix} Error handling match status update: ${e}`);
    return false;
  });

  if (!ok) {
    res.status(500).json({ error: "Failed to process match status update" });
    return;
  }
  res.json({ status: "ok" });
});

// Being kept for backwards compatibility with older OVS versions, can be removed eventually
app.post("/mvsi_register", async (req, res, next) => {
  logger.info(`${logPrefix} GET REGISTRY call from rollback server`);
  const body = req.body;
  const config = await redisGetMatchConfig(body.matchId);
  if (!config || !config.matchKey || !body || !body.key || config.matchKey !== body.key) {
    res.send("");
    return;
  }
  const players = config.players.map((p) => {
    return {
      player_index: p.playerIndex,
      ip: p.ip,
      is_host: p.isHost,
    };
  });
  res.json({
    max_players: config.players.length,
    match_duration: 36000,
    players,
  });

  // Same as /ovs_register — tell game clients to connect after rollback server is ready
  const playerIds = config.players.map((p) => p.playerId);
  await redisGameServerInstanceReady(body.matchId, playerIds);
  logger.info(`${logPrefix} Sent game-server-instance-ready for match ${body.matchId} after rollback server fetched config (legacy)`);
});

// Being kept for backwards compatibility with older OVS versions, can be removed eventually
app.post("/mvsi_end_match", async (req, res, next) => {
  logger.info("mvsi_end_match");
  const body = req.body;
  const config = await redisGetMatchConfig(body.matchId);

  if (!config || !config.matchKey || !body || !body.key || config.matchKey !== body.key) {
    res.send("");
    return;
  }
  if (config) {
    await redisPublisdEndOfMatch(
      config.players.map((p) => p.playerId),
      config.matchId,
    );
    res.send("");
  }
});

// ============================================================
// Party Web Page — Join parties via shareable key
// ============================================================

app.get("/party", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, "");
    const { player: resolvedPlayer, pickerShown } = await resolvePlayerForWeb(req, res, "/party");
    if (pickerShown) return;
    let player = resolvedPlayer;
    const username = player?.name || "Unknown";
    const currentKey = player?.party_key || "";

    const html = partyTemplate({
      username,
      currentKey,
      error: null,
      success: null,
    });
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /party: ${e}`);
    res.status(500).send("Error loading party page");
  }
});

app.post("/party/set-key", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const { player: resolvedPlayer, pickerShown } = await resolvePlayerForWeb(req, res, "/party");
    if (pickerShown) return;
    let player = resolvedPlayer;
    if (!player) {
      res.send(partyTemplate({ username: "Unknown", currentKey: "", error: "You must be connected to the game first.", success: null }));
      return;
    }

    const newKey = (req.body.key || "").trim();

    // Validate key
    if (!newKey || newKey.length < 3) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "Key must be at least 3 characters.", success: null }));
      return;
    }
    if (newKey.length > 20) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "Key must be 20 characters or less.", success: null }));
      return;
    }
    if (!/^[a-zA-Z0-9_\-]+$/.test(newKey)) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "Key can only contain letters, numbers, underscores, and dashes.", success: null }));
      return;
    }

    // Check if key is already taken by another player
    const existingKeyData = await redisGetPartyKey(newKey);
    if (existingKeyData && existingKeyData.playerId !== player.id) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "That key is already taken by another player.", success: null }));
      return;
    }

    // Delete old key from Redis if changing
    const oldKey = player.party_key;
    if (oldKey && oldKey.toLowerCase() !== newKey.toLowerCase()) {
      await redisDeletePartyKey(oldKey);
    }

    // Save new key to MongoDB
    player.party_key = newKey;
    await player.save();

    // Update Redis connection hashes
    await redisClient.hSet(`connections:${player.id}`, { party_key: newKey });
    await redisClient.hSet(`connections:${ip}`, { party_key: newKey });

    // Save party key lookup in Redis (lobbyId from current lobby or empty)
    const conn = (await redisClient.hGetAll(`connections:${player.id}`)) as any;
    const currentLobbyId = conn?.lobby_id || "";
    await redisSavePartyKey(newKey, { playerId: player.id, lobbyId: currentLobbyId, username: player.name });

    logger.info(`${logPrefix} Player ${player.name} (${ip}) set party key to "${newKey}"`);
    res.send(partyTemplate({ username: player.name, currentKey: newKey, error: null, success: `Party key set to "${newKey}"!` }));
  } catch (e) {
    logger.error(`${logPrefix} Error in POST /party/set-key: ${e}`);
    res.status(500).send("Error setting party key");
  }
});

app.post("/party/join", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const { player: resolvedPlayer, pickerShown } = await resolvePlayerForWeb(req, res, "/party");
    if (pickerShown) return;
    let player = resolvedPlayer;
    if (!player) {
      res.send(partyTemplate({ username: "Unknown", currentKey: "", error: "You must be connected to the game first.", success: null }));
      return;
    }

    const joinKey = (req.body.key || "").trim();
    if (!joinKey) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "Please enter a party key.", success: null }));
      return;
    }

    // Look up the party key in Redis
    const keyData = await redisGetPartyKey(joinKey);
    if (!keyData) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `No player found with party key "${joinKey}". They must be in-game.`, success: null }));
      return;
    }

    // Can't join your own party
    if (keyData.playerId === player.id) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "That's your own party key!", success: null }));
      return;
    }

    const lobbyId = keyData.lobbyId;
    if (!lobbyId) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `${keyData.username} doesn't have an active lobby yet. They need to be in-game.`, success: null }));
      return;
    }

    // Get the lobby state
    const lobby = await redisGetLobbyState(lobbyId);
    if (!lobby) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `${keyData.username}'s lobby is no longer active. They may need to restart their game.`, success: null }));
      return;
    }

    // Check if already in this lobby
    if (lobby.playerIds.includes(player.id)) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `You're already in ${keyData.username}'s lobby!`, success: null }));
      return;
    }

    // Block if target player is already in a party (lobby has 2+ players)
    if (lobby.playerIds.length >= 2) {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `${keyData.username} is already in a party with someone else.`, success: null }));
      return;
    }

    // Block if target player is in a match or searching for one
    const targetStatus = await redisClient.hGet(`player:${keyData.playerId}`, "status");
    if (targetStatus === "queued") {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `${keyData.username} is currently searching for a match. They need to cancel first.`, success: null }));
      return;
    }
    if (targetStatus === "in_match") {
      res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: `${keyData.username} is currently in a match. Wait for them to finish.`, success: null }));
      return;
    }

    // Block if the JOINING player is already in a party with someone else
    const joinerLobbyId = await redisGetPlayerLobby(player.id);
    if (joinerLobbyId) {
      const joinerLobby = await redisGetLobbyState(joinerLobbyId);
      if (joinerLobby && joinerLobby.playerIds.length >= 2) {
        res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: "You're already in a party. Leave your current party first.", success: null }));
        return;
      }
    }

    // Add the joining player to the lobby and force 2v2 mode
    lobby.playerIds.push(player.id);
    if (lobby.playerIds.length >= 2) {
      lobby.mode = "2v2";
    }
    await redisSaveLobbyState(lobbyId, lobby);
    await redisSavePlayerLobby(player.id, lobbyId);

    // Trigger lobby rejoin for ALL players (force-closes WebSocket, game reconnects with updated roster)
    for (const pid of lobby.playerIds) {
      const rejoinNotification: RedisLobbyRejoinNotification = {
        playerId: pid,
        lobbyId,
      };
      await redisPublishLobbyRejoin(rejoinNotification);
    }

    logger.info(`${logPrefix} Player ${player.name} (${ip}) joined ${keyData.username}'s lobby ${lobbyId} via party key "${joinKey}". Players: ${lobby.playerIds.join(", ")}`);
    res.send(partyTemplate({ username: player.name, currentKey: player.party_key || "", error: null, success: `Joined ${keyData.username}'s party! Check your game.` }));
  } catch (e) {
    logger.error(`${logPrefix} Error in POST /party/join: ${e}`);
    res.status(500).send("Error joining party");
  }
});

// Password matchmaking removed — use /custom for custom lobbies instead

// ============================================================
// DLL invite accept — IP-based (no JWT), called by the DLL
// when the player clicks "Accept" on the party invite dialog.
// Follows the same pattern as POST /party/join above.
// ============================================================

app.put("/ovs/accept-invite/:lobbyId", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const lobbyId = req.params.lobbyId;

    // Resolve via JWT/Steam/Epic/HW/IP (household-safe); then load by account id.
    const conn = await resolveAccountFromRequest(req);
    const player = conn?.id
      ? await PlayerTesterModel.findOne({ _id: new Types.ObjectId(conn.id) })
      : null;
    if (!player) {
      res.status(401).json({ error: "not_connected" });
      return;
    }

    const lobby = await redisGetLobbyState(lobbyId);
    if (!lobby) {
      res.status(404).json({ error: "lobby_not_found" });
      return;
    }

    if (lobby.playerIds.includes(player.id)) {
      res.json({ success: true, lobbyId }); // idempotent
      return;
    }

    if (lobby.playerIds.length >= 2) {
      res.status(409).json({ error: "lobby_full" });
      return;
    }

    // Block if lobby owner is queued or in a match
    const ownerStatus = await redisClient.hGet(`player:${lobby.ownerId}`, "status");
    if (ownerStatus === "queued") {
      res.status(409).json({ error: "owner_queued" });
      return;
    }
    if (ownerStatus === "in_match") {
      res.status(409).json({ error: "owner_in_match" });
      return;
    }

    // If joining player is already in a party, auto-leave it first.
    // The old partner goes solo, then this player joins the new lobby.
    const joinerLobbyId = await redisGetPlayerLobby(player.id);
    if (joinerLobbyId) {
      const joinerLobby = await redisGetLobbyState(joinerLobbyId);
      if (joinerLobby && joinerLobby.playerIds.length >= 2) {
        logger.info(`${logPrefix} Player ${player.id} is in party ${joinerLobbyId} — auto-leaving before accepting invite`);
        await performGenuineLeave(player.id, joinerLobbyId, joinerLobby);
      }
    }

    // Add player to lobby and force 2v2
    lobby.playerIds.push(player.id);
    if (lobby.playerIds.length >= 2) {
      lobby.mode = "2v2";
    }
    await redisSaveLobbyState(lobbyId, lobby);
    // DON'T change player_lobby here — it still points to the invitee's old solo lobby.
    // The game's JoinLobby will call leave_player_lobby first (to leave the old solo lobby),
    // and if player_lobby pointed at the shared lobby, leave_player_lobby would destroy it.
    // Instead, store the target in pending_join_lobby — join_party_lobby reads it.
    await redisSetPendingJoinLobby(player.id, lobbyId);

    logger.info(`${logPrefix} Player ${player.name} (${ip}) accepted invite to lobby ${lobbyId}. Players: ${lobby.playerIds.join(", ")}`);
    res.json({ success: true, lobbyId });

    // Inviter notification happens LATER — when the invitee's game calls
    // join_party_lobby SSC and completes loading. The SSC handler sends
    // a single party_joined DLL notification at the right time.
  } catch (e) {
    logger.error(`${logPrefix} Error in PUT /ovs/accept-invite: ${e}`);
    res.status(500).json({ error: "internal_error" });
  }
});

// ============================================================
// Leaderboard — Top 100 rankings for 1v1 and 2v2
// ============================================================

app.get("/leaderboard", async (req, res) => {
  try {
    const html = leaderboardTemplate({});
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /leaderboard: ${e}`);
    res.status(500).send("Error loading leaderboard");
  }
});

// ============================================================
// Live Matches — Current ranked sets in progress
// ============================================================
//
// Server-wide shared cache refreshed on a 2s tick (same cadence as the
// matchmaking worker). All /api/matches requests return the same cached
// snapshot, so cost is O(1) with viewer count instead of O(N). The tick
// does one Redis sweep regardless of how many tabs are polling.

const MATCHES_CACHE_TICK_MS = 2000;
let matchesCache: { matches: any[]; count: number; generatedAt: number } = {
  matches: [],
  count: 0,
  generatedAt: 0,
};

async function refreshMatchesCache(): Promise<void> {
  try {
    // Two scans merged by setId:
    // Pass 1 — match_started:* catches games actively playing (including 0-0 game 1).
    // Pass 2 — ranked_set:* catches sets alive in the inter-game check-in phase
    //          where no game is currently running but the set hasn't ended.
    // Both scans dedupe against a shared seenSetIds set; keys are written together
    // at set creation (websocket.handleOnMatchEnd), so setId always matches across passes.
    const matchIds = await redisGetInProgressMatches();
    const activeSets = await redisGetActiveRankedSets();
    const results: any[] = [];
    const seenSetIds = new Set<string>();

    // Helper: build `teams: {"0":[...], "1":[...]}` from a players array + characters map
    const buildTeams = async (players: any[], matchChars: Record<string, string>): Promise<Record<string, any[]>> => {
      const teams: Record<string, any[]> = { "0": [], "1": [] };
      for (const p of players) {
        if (p.isSpectator) continue;
        const conn = await redisClient.hGetAll(`connections:${p.playerId}`) as any;
        // Fallback chain: connections.username (preferred — set on /access from
        // player.name) → connections.hydraUsername (auto-gen, always populated)
        // → "Unknown". The hydraUsername fallback handles the case where the
        // connection hash got partial-updated and lost the username field, OR
        // where Mongo's name was renamed via /namechange but the Redis hash
        // hasn't been refreshed by a follow-up /access yet.
        const username = conn?.username || conn?.hydraUsername || "Unknown";
        const character = conn?.character || matchChars[p.playerId] || "unknown";
        const team = String(p.teamIndex ?? 0);
        if (!teams[team]) teams[team] = [];
        teams[team].push({ playerId: p.playerId, username, character });
      }
      return teams;
    };

    // ─── Pass 1: games actively playing (match_started:*) ───
    for (const matchId of matchIds) {
      const matchConfig = await redisGetMatchConfig(matchId);
      if (!matchConfig || !Array.isArray(matchConfig.players)) continue;
      if (matchConfig.isCustomGame) continue;

      // Resolve setId via player_ranked_set mapping. For game 1 (no set yet),
      // setId defaults to matchId.
      let setId = matchId;
      let setState: any = null;
      const anyPlayerId = matchConfig.players[0]?.playerId;
      if (anyPlayerId) {
        const mappedSetId = await redisClient.get(`player_ranked_set:${anyPlayerId}`);
        if (mappedSetId) setId = mappedSetId;
      }
      if (seenSetIds.has(setId)) continue;
      seenSetIds.add(setId);

      const setRaw = await redisClient.get(`ranked_set:${setId}`);
      if (setRaw) {
        try { setState = JSON.parse(setRaw); } catch {}
      }

      const matchCharsRaw = await redisClient.get(`match_characters:${setId}`);
      const matchChars = matchCharsRaw ? JSON.parse(matchCharsRaw) : {};
      const teams = await buildTeams(matchConfig.players, matchChars);

      results.push({
        setId,
        matchId,
        mode: matchConfig.mode,
        scores: setState?.scores || [0, 0],
        gamesPlayed: setState?.gamesPlayed || 0,
        conceded: !!setState?.conceded,
        teams,
      });
    }

    // ─── Pass 2: active sets without a currently-playing game (inter-game) ───
    for (const { setId, state: setState } of activeSets) {
      if (seenSetIds.has(setId)) continue;
      if (!setState || !Array.isArray(setState.players)) continue;
      seenSetIds.add(setId);

      const matchCharsRaw = await redisClient.get(`match_characters:${setId}`);
      const matchChars = matchCharsRaw ? JSON.parse(matchCharsRaw) : {};
      const teams = await buildTeams(setState.players, matchChars);

      results.push({
        setId,
        matchId: setId, // no active game; use setId as the display id
        mode: setState.mode,
        scores: setState.scores || [0, 0],
        gamesPlayed: setState.gamesPlayed || 0,
        conceded: !!setState.conceded,
        teams,
      });
    }

    matchesCache = { matches: results, count: results.length, generatedAt: Date.now() };
  } catch (e) {
    logger.error(`${logPrefix} refreshMatchesCache error: ${e}`);
  }
}

// Refresh loop — interval handles it; no boot call needed (Redis may not be connected yet).
setInterval(refreshMatchesCache, MATCHES_CACHE_TICK_MS);

app.get("/matches", async (req, res) => {
  try {
    const html = matchesTemplate({});
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /matches: ${e}`);
    res.status(500).send("Error loading matches");
  }
});

app.get("/api/matches", async (req, res) => {
  // Return the shared cache — refreshed by the 2s tick above.
  res.json(matchesCache);
});

app.post("/api/identify", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const clean = (v: any) => (typeof v === "string" && v !== "Unknown" ? v : "");
    const { steamId: _s = "", epicId: _e = "", hardwareId: _h = "" } = req.body ?? {};
    const steamId = clean(_s), epicId = clean(_e), hardwareId = clean(_h);
    if (!ip) {
      res.status(400).json({ error: "Could not determine IP" });
      return;
    }
    await redisSaveIdentity(ip, steamId, epicId, hardwareId);
    logger.info(`${logPrefix} Identity registered for IP ${ip} — steam:${steamId} epic:${epicId} hw:${hardwareId.slice(0, 8)}...`);

    // Construct an OVS-side JWT and return it so the DLL can attach it to
    // un-authenticated polling routes (NotificationPoller, etc.). This avoids
    // IP-collision bugs where two players behind one public IP would drain
    // each other's notification queues. DLL stores the token and sends it
    // as x-hydra-access-token on /ovs/notifications. The server's resolver
    // decodes the claims at step 1/2/3/4 (id → steam → epic → hw) before
    // falling back to IP.
    //
    // Try to resolve an existing account id for these identifiers so the
    // JWT's `id` claim is populated where possible (resolver fast path).
    // First-time signups haven't hit /access yet → id is empty but steam/
    // epic/hw still resolve via the identity indexes we wrote at last login.
    let resolvedId = "";
    try {
      if (steamId) {
        const byId = await redisClient.get(`identity:steam:${steamId}`);
        if (byId) resolvedId = byId;
      }
      if (!resolvedId && epicId) {
        const byId = await redisClient.get(`identity:epic:${epicId}`);
        if (byId) resolvedId = byId;
      }
      if (!resolvedId && hardwareId) {
        const byId = await redisClient.get(`identity:hw:${hardwareId}`);
        if (byId) resolvedId = byId;
      }
    } catch (lookupErr) {
      logger.error(`${logPrefix} /api/identify: identity-index lookup failed: ${lookupErr}`);
    }

    const jwtLib = require("jsonwebtoken");
    const claims = {
      // Full AccountToken-compatible shape so the resolver hits JWT id fast path
      id: resolvedId,
      steamId,
      epicId,
      hardwareId,
      current_ip: ip,
      // Leave unrelated fields empty — resolver only consults id/steamId/epicId/hardwareId
      profile_id: "",
      public_id: "",
      wb_network_id: resolvedId,
      username: "",
      hydraUsername: "",
      lobby_id: "",
      GameplayPreferences: 964,
    };
    // 30 days so the DLL doesn't need to refresh during a session. Short-lived
    // mid-session re-keying would break NotifPoller's long-running background thread.
    const token = jwtLib.sign(claims, SECRET, { expiresIn: "30d" });

    res.json({ ok: true, token, accountId: resolvedId || null });
  } catch (e) {
    logger.error(`${logPrefix} Error in POST /api/identify: ${e}`);
    res.status(500).json({ error: "Internal error" });
  }
});

app.get("/api/leaderboard/:mode", async (req, res) => {
  try {
    const mode = req.params.mode as "1v1" | "2v2";
    if (mode !== "1v1" && mode !== "2v2") {
      res.status(400).json({ error: "Invalid mode. Use '1v1' or '2v2'." });
      return;
    }

    const players = await getLeaderboard(mode, 100);
    res.json({ players });
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /api/leaderboard: ${e}`);
    res.status(500).json({ error: "Error fetching leaderboard" });
  }
});

app.get("/api/leaderboard/:mode/me", async (req, res) => {
  try {
    const mode = req.params.mode as "1v1" | "2v2";
    if (mode !== "1v1" && mode !== "2v2") {
      res.status(400).json({ error: "Invalid mode. Use '1v1' or '2v2'." });
      return;
    }
    const player = await getPlayerFromReq(req);
    if (!player) {
      res.json({ error: "Not connected to game" });
      return;
    }
    const rank = await getPlayerRank(player.id, mode);
    if (!rank) {
      res.json({ error: "No ranked games played" });
      return;
    }
    res.json(rank);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /api/leaderboard/me: ${e}`);
    res.status(500).json({ error: "Error fetching player rank" });
  }
});

// ============================================================
// DLL Version Check — GitHub Releases API
// ============================================================

let cachedGitHubRelease: { data: any; fetchedAt: number } | null = null;
const GITHUB_CACHE_TTL = 5 * 60 * 1000; // 5 minutes

app.get("/ovs/client-version", async (req, res) => {
  try {
    const clientVersion = (req.query.v as string) || "";

    // Fetch latest release from GitHub (cached)
    if (!cachedGitHubRelease || Date.now() - cachedGitHubRelease.fetchedAt > GITHUB_CACHE_TTL) {
      const ghRes = await fetch("https://api.github.com/repos/openversus/ovs-client/releases/latest", {
        headers: { "User-Agent": "OpenVersus-Server", "Accept": "application/vnd.github+json" },
      });
      if (!ghRes.ok) {
        logger.warn(`${logPrefix} GitHub API returned ${ghRes.status}`);
        res.json({ latest_version: clientVersion, download_url: "", is_latest: true, release_name: "" });
        return;
      }
      const data = await ghRes.json();
      cachedGitHubRelease = { data, fetchedAt: Date.now() };
      logger.info(`${logPrefix} Cached GitHub release: ${data.tag_name || data.name}`);
    }

    const release = cachedGitHubRelease.data;
    const latestVersion = (release.tag_name || release.name || "").replace(/^v/i, "");
    const assets: any[] = release.assets || [];

    // Find the .asi asset (or .zip fallback)
    const asiAsset = assets.find((a: any) => a.name.endsWith(".asi"))
      || assets.find((a: any) => a.name.endsWith(".zip"));
    const downloadUrl = asiAsset?.browser_download_url || "";

    const isLatest = clientVersion === latestVersion;

    if (!isLatest && clientVersion) {
      logger.info(`${logPrefix} Client version ${clientVersion} is outdated (latest: ${latestVersion})`);
    }

    res.json({
      latest_version: latestVersion,
      download_url: downloadUrl,
      is_latest: isLatest,
      release_name: release.name || "",
    });
  } catch (e) {
    logger.error(`${logPrefix} Error in /ovs/client-version: ${e}`);
    res.json({ latest_version: "", download_url: "", is_latest: true, release_name: "" });
  }
});

// ============================================================
// /ovs/notifications — DLL polls this every 2s to receive queued
// notifications (match_cancel, party invites, toasts, etc.)
// Player is identified by IP via connections:{ip} Redis hash.
// Returns JSON array (empty [] if no pending).
// ============================================================
app.get("/ovs/notifications", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    if (!ip || ip === "") {
      res.json([]);
      return;
    }

    // Look up the player — tries JWT, SteamID, EpicID, HardwareID, then IP fallback.
    // Use the with-source variant so the delivery log can show which path resolved.
    const { conn, source } = await resolveAccountWithSource(req);
    const playerId = conn?.id;
    if (!playerId) {
      res.json([]);
      return;
    }

    // Pop all queued DLL notifications for this player
    const notifications = await redisPopDLLNotifications(playerId);
    if (notifications.length > 0) {
      logger.info(`${logPrefix} Delivered ${notifications.length} DLL notification(s) to ${playerId} via=${source} (IP ${ip})`);
    }
    res.json(notifications);
  } catch (e) {
    logger.error(`${logPrefix} Error in /ovs/notifications: ${e}`);
    res.json([]);
  }
});

// ============================================================
// Admin banner — simple HTML page to push a notification banner
// to all connected players (or a single player by ID). The DLL
// polls /ovs/notifications and any entry with type="admin_banner"
// pops a two-line in-game notification widget.
// ============================================================

// Admin auth — simple password gate via query param or session cookie.
// Usage: /admin/banner?pw=yourpassword (sets cookie for subsequent requests)
const ADMIN_PW = env.ADMIN_PASSWORD || "changeme";
app.use("/admin", (req, res, next) => {
  // Check query param first (login link)
  if (req.query.pw === ADMIN_PW) {
    res.cookie("admin_auth", ADMIN_PW, { httpOnly: true, maxAge: 24 * 60 * 60 * 1000 });
    return next();
  }
  // Check cookie (subsequent requests)
  if (req.cookies?.admin_auth === ADMIN_PW) {
    return next();
  }
  res.status(401).send(`
    <html><body style="font-family:Arial;display:grid;place-items:center;height:100vh;margin:0;background:#111;color:#fff">
      <form style="text-align:center">
        <h2>Admin Login</h2>
        <input name="pw" type="password" placeholder="Password" style="padding:10px;font-size:16px;border-radius:8px;border:none;margin:8px"/>
        <button type="submit" style="padding:10px 24px;font-size:16px;border-radius:8px;border:none;background:#ff7500;color:#fff;cursor:pointer">Login</button>
      </form>
    </body></html>
  `);
});
app.use("/api/admin", (req, res, next) => {
  if (req.cookies?.admin_auth === ADMIN_PW || req.query.pw === ADMIN_PW) return next();
  res.status(401).json({ error: "Unauthorized" });
});

// Serve the HTML admin page.
app.get("/admin/banner", async (req, res) => {
  try {
    const onlineCount = (await redisGetOnlinePlayers()).length;
    res.send(adminBannerTemplate({ onlineCount }));
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /admin/banner: ${e}`);
    res.status(500).send("Error loading admin banner page");
  }
});

// Lightweight count endpoint used by the admin page's "Refresh" button.
app.get("/api/admin/banner/online-count", async (req, res) => {
  try {
    const players = await redisGetOnlinePlayers();
    res.json({ count: players.length });
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /api/admin/banner/online-count: ${e}`);
    res.status(500).json({ error: String(e) });
  }
});

// POST endpoint that actually pushes the notification.
// Body: { title?, message?, timeout?, targetPlayerId? }
// If targetPlayerId is omitted, broadcasts to every online player.
app.post("/api/admin/banner", async (req, res) => {
  try {
    const body = req.body || {};
    const title = (body.title || "").toString().slice(0, 255);
    const message = (body.message || "").toString().slice(0, 500);
    const timeout = Number.isFinite(body.timeout) ? Number(body.timeout) : 8;
    const targetPlayerId = body.targetPlayerId ? body.targetPlayerId.toString() : null;

    if (!title && !message) {
      res.status(400).json({ error: "title or message is required" });
      return;
    }

    const notification = {
      type: "admin_banner",
      title,
      message,
      data: { timeout },
      timestamp: Date.now(),
    };

    let targets: string[] = [];
    if (targetPlayerId) {
      targets = [targetPlayerId];
    } else {
      targets = await redisGetOnlinePlayers();
    }

    if (targets.length === 0) {
      res.json({ ok: true, delivered: 0, note: "No online players" });
      return;
    }

    let delivered = 0;
    for (const pid of targets) {
      try {
        await redisPushDLLNotification(pid, notification);
        delivered++;
      } catch (e) {
        logger.error(`${logPrefix} Failed to push admin banner to ${pid}: ${e}`);
      }
    }

    logger.info(`${logPrefix} Admin banner broadcast: delivered to ${delivered}/${targets.length} players — title="${title}" message="${message}"`);
    res.json({ ok: true, delivered, attempted: targets.length });
  } catch (e) {
    logger.error(`${logPrefix} Error in POST /api/admin/banner: ${e}`);
    res.status(500).json({ error: String(e) });
  }
});

// ============================================================
// Custom Lobby — Web-based custom game lobbies
// ============================================================

// Helper: identify player for browser/AJAX calls via IP candidates + picker cookie.
// - 1 account on IP → use it.
// - 2+ accounts on IP → require the signed `ovs_web_account` cookie (set by /account/switch).
//   Without the cookie, returns null — AJAX caller will get a 401 and the user should visit
//   /custom (or /party / /namechange) once in a browser to pick an account.
async function getPlayerFromReq(req: any): Promise<{ id: string; username: string; ip: string } | null> {
  const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
  const accounts = await PlayerTesterModel.find({ ip }).lean();
  if (accounts.length === 0) return null;

  let picked: any | null = null;
  if (accounts.length === 1) {
    picked = accounts[0];
  } else {
    const claim = verifyAdminCookie(req.cookies?.[ADMIN_COOKIE]);
    if (claim && claim.ip === ip) {
      const stale = await isAdminCookieStale(claim, ip);
      if (!stale) {
        picked = accounts.find((a: any) => String(a._id) === claim.accountId) || null;
      } else {
        logger.info(`${logPrefix} getPlayerFromReq: cookie stale for IP ${ip} — user must re-pick account`);
      }
    }
    if (!picked) {
      logger.info(`${logPrefix} getPlayerFromReq: ${accounts.length} accounts on IP ${ip}, no valid picker cookie — returning null (user should visit /custom to pick)`);
      return null;
    }
  }
  return { id: String(picked._id), username: picked.name || "Unknown", ip };
}

// ============================================================
// Admin web auth — cookie-based "which account are you" picker
// ============================================================
// Browser-facing admin pages (/namechange, /party, /custom) have no JWT. In the
// single-user-per-IP case the identity resolver's IP fallback picks the right
// account. But in a household, two accounts share an IP — without a picker the
// server would arbitrarily pick one and let the user mutate the wrong player.
//
// Flow:
//   1. resolvePlayerForWeb() finds all Mongo accounts with ip === req.ip
//   2. If 0 → null (caller should handle: redirect, 401, or create-on-demand)
//   3. If 1 → return that player
//   4. If 2+ → check signed cookie `ovs_web_account`. If cookie's accountId is
//      one of the candidates AND its steamId/hardware matches Mongo, use it.
//      Otherwise render the picker page; caller just returns.
// ============================================================
const ADMIN_COOKIE = "ovs_web_account";
const ADMIN_COOKIE_TTL = 60 * 60 * 24 * 30; // 30 days — the real invalidation is event-based (below)

function signAdminCookie(accountId: string, ip: string): string {
  const jwt = require("jsonwebtoken");
  // `iat` is auto-set by jsonwebtoken in seconds-since-epoch.
  return jwt.sign({ accountId, ip }, SECRET, { expiresIn: ADMIN_COOKIE_TTL });
}

function verifyAdminCookie(token: string | undefined): { accountId: string; ip: string; iat?: number } | null {
  if (!token) return null;
  try {
    const jwt = require("jsonwebtoken");
    return jwt.verify(token, SECRET) as { accountId: string; ip: string; iat?: number };
  } catch {
    return null;
  }
}

/**
 * Returns true if the cookie was issued BEFORE the last time the IP's account
 * set changed — meaning it's stale and the user must re-verify.
 */
async function isAdminCookieStale(claim: { iat?: number }, ip: string): Promise<boolean> {
  if (!claim.iat) return true; // malformed
  const issuedAtMs = claim.iat * 1000;
  const changedAtStr = await redisClient.get(`admin:ip_changed_at:${ip}`);
  if (!changedAtStr) return false; // no change ever recorded → cookie is fine
  const changedAtMs = Number(changedAtStr);
  return issuedAtMs < changedAtMs;
}

/**
 * Resolve which player this browser request is acting on.
 * @returns
 *   - `{ player: <doc> }` on success (single match, or cookie match, or only one account)
 *   - `{ pickerShown: true }` if multi-account and picker page was rendered. Caller should `return`.
 *   - `{ player: null }` if no accounts at all — caller handles (redirect / create / error).
 */
async function resolvePlayerForWeb(
  req: express.Request,
  res: express.Response,
  returnTo: string,
): Promise<{ player: any | null; pickerShown: boolean }> {
  const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP

  // Candidate accounts at this IP
  const accounts = await PlayerTesterModel.find({ ip }).lean();

  if (accounts.length === 0) {
    return { player: null, pickerShown: false };
  }
  if (accounts.length === 1) {
    // Single-user-per-IP: return directly, also set the cookie so subsequent reqs skip
    // the Mongo candidate query.
    const only = accounts[0] as any;
    res.cookie(ADMIN_COOKIE, signAdminCookie(String(only._id), ip), {
      maxAge: ADMIN_COOKIE_TTL * 1000,
      httpOnly: true,
      sameSite: "lax",
    });
    const player = await PlayerTesterModel.findById(only._id);
    return { player, pickerShown: false };
  }

  // 2+ accounts on this IP — check the signed cookie
  const cookieToken = (req as any).cookies?.[ADMIN_COOKIE];
  const claim = verifyAdminCookie(cookieToken);
  if (claim && claim.ip === ip) {
    const stale = await isAdminCookieStale(claim, ip);
    if (!stale) {
      const match = accounts.find(a => String((a as any)._id) === claim.accountId);
      if (match) {
        const player = await PlayerTesterModel.findById((match as any)._id);
        return { player, pickerShown: false };
      }
    } else {
      logger.info(`${logPrefix} Admin cookie stale for IP ${ip} (new account arrived since issue) — re-showing picker`);
    }
  }

  // Render picker
  const html = accountPickerTemplate({
    returnTo,
    accounts: accounts.map(a => {
      const hw = (a as any).hardwareId || "";
      return {
        id: String((a as any)._id),
        name: (a as any).name || "Unknown",
        steamId: (a as any).steamId || "",
        epicId: (a as any).epicId || "",
        hardwareId: hw ? hw.substring(0, 8) + "..." : "",
      };
    }),
  });
  res.send(html);
  return { player: null, pickerShown: true };
}

// POST /account/switch — user clicked "Use This" on the picker.
// Step 1 of verification: the user claims an account. We:
//   1. Verify the account exists at this IP (cheap gate).
//   2. Verify the account is currently logged in (has a Redis `connections:${id}` record).
//      — If not online, reject: can't prove ownership of an account nobody's using.
//   3. Generate a 6-digit verification code, store in Redis `admin:verify:${verifyId}`
//      (with random verifyId so the URL doesn't leak the code).
//   4. Push the code as an `admin_banner` in-game notification — only the real owner
//      of that account sees it, because only their DLL is polling /ovs/notifications.
//   5. Show the verify page with a code input.
//
// Step 2 happens in POST /account/verify.
app.post("/account/switch", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const accountId = (req.body?.accountId || "").trim();
    const rawReturnTo = (req.body?.returnTo || "/home").trim();
    const returnTo = rawReturnTo.startsWith("/") && !rawReturnTo.startsWith("//") ? rawReturnTo : "/home";

    if (!accountId || !Types.ObjectId.isValid(accountId)) {
      res.status(400).send("Invalid accountId");
      return;
    }

    // Helper: re-render the picker page with an error banner (keeps user in-flow).
    const renderPickerWithError = async (errMsg: string) => {
      const candidates = await PlayerTesterModel.find({ ip }).lean();
      const html = accountPickerTemplate({
        returnTo,
        error: errMsg,
        accounts: candidates.map(a => {
          const hw = (a as any).hardwareId || "";
          return {
            id: String((a as any)._id),
            name: (a as any).name || "Unknown",
            steamId: (a as any).steamId || "",
            epicId: (a as any).epicId || "",
            hardwareId: hw ? hw.substring(0, 8) + "..." : "",
          };
        }),
      });
      res.send(html);
    };

    // 1. Verify the accountId has ip === req.ip (cheap gate against random accountIds)
    const match = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(accountId), ip });
    if (!match) {
      await renderPickerWithError("That account isn't recognized on this network. Pick one from the list below.");
      return;
    }

    // 2. Verify account is currently online (has live Redis session). If the account
    //    isn't logged in via the DLL right now, nobody can receive the verification
    //    notification — which means whoever's asking isn't the owner.
    const liveSession = await redisClient.hGetAll(`connections:${accountId}`) as any;
    if (!liveSession || !liveSession.id) {
      await renderPickerWithError(
        `${match.name || "That account"} isn't signed in to the game right now — please log in to the main menu with that account, then click "Use This" again.`,
      );
      return;
    }

    // 3. Generate 6-digit code + opaque verifyId (the verifyId is what's in the form, not the code)
    const code = String(randomInt(100000, 1000000));
    const verifyId = randomBytes(16).toString("hex");
    const payload = { accountId, ip, returnTo, code, createdAt: Date.now() };
    await redisClient.set(`admin:verify:${verifyId}`, JSON.stringify(payload), { EX: 60 * 5 }); // 5 min code TTL
    await redisClient.set(`admin:verify:attempts:${verifyId}`, "0", { EX: 60 * 5 });

    // 4. Push the code as an in-game banner notification. DLL's NotifPoller picks it up
    //    from Redis within ~2s and shows it as a two-line in-game toast.
    //    `type: "admin_banner"` is the DLL-side notification type (preserves the existing
    //    in-game widget), not a claim that this is an admin action — it's player account verification.
    await redisPushDLLNotification(accountId, {
      type: "admin_banner",
      title: "Web Login Code",
      message: `Enter ${code} in the browser to verify.`,
      data: { timeout: 120 }, // 2 min on-screen. Code itself is valid for 5 min (Redis TTL above).
      timestamp: Date.now(),
    });

    logger.info(`${logPrefix} Account verify code pushed to ${accountId} (IP ${ip}, banner 2min, code valid 5min)`);

    // 5. Render the verify page. verifyId is hidden in the form.
    const html = accountVerifyTemplate({
      verifyId,
      returnTo,
      accountName: match.name || "Unknown",
      steamId: match.steamId || "",
      error: null,
    });
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in /account/switch: ${e}`);
    res.status(500).send("Error starting verification");
  }
});

// POST /account/verify — user entered the code from the in-game banner.
// On success: sets the signed cookie, deletes the verify record, redirects to returnTo.
// On failure: increments attempt counter, re-renders the page with an error.
// After 5 failed attempts the verify record is deleted entirely (user has to restart).
app.post("/account/verify", async (req, res) => {
  try {
    const ip = tryGetRealIP(req).replace(/^::ffff:/, ""); // Ensure we get the real IP for name changes, not just the direct connection IP
    const verifyId = (req.body?.verifyId || "").toString().trim();
    const submittedCode = (req.body?.code || "").toString().trim();

    if (!verifyId || !/^[a-f0-9]{32}$/i.test(verifyId)) {
      res.status(400).send("Invalid verify request");
      return;
    }
    if (!submittedCode || !/^\d{6}$/.test(submittedCode)) {
      // Bad code format — re-render with error (if the record still exists)
      const raw = await redisClient.get(`admin:verify:${verifyId}`);
      if (raw) {
        const p = JSON.parse(raw);
        const acct = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(p.accountId) }).lean() as any;
        res.send(accountVerifyTemplate({
          verifyId,
          returnTo: p.returnTo || "/home",
          accountName: acct?.name || "Unknown",
          steamId: acct?.steamId || "",
          error: "Code must be 6 digits.",
        }));
        return;
      }
      res.status(400).send("Invalid or expired verification. Go back and try again.");
      return;
    }

    const raw = await redisClient.get(`admin:verify:${verifyId}`);
    if (!raw) {
      res.status(400).send("Verification expired or already used. Go back and try again.");
      return;
    }
    const payload = JSON.parse(raw) as { accountId: string; ip: string; returnTo: string; code: string; createdAt: number };

    // Security: the IP of the verify must match the request IP. Prevents someone else
    // on the LAN from stealing a code if they somehow learn the verifyId.
    if (payload.ip !== ip) {
      await redisClient.del(`admin:verify:${verifyId}`);
      await redisClient.del(`admin:verify:attempts:${verifyId}`);
      res.status(403).send("IP mismatch on verification. Start over from the picker.");
      return;
    }

    // Attempt limiter
    const attempts = Number(await redisClient.get(`admin:verify:attempts:${verifyId}`) || "0");
    if (attempts >= 5) {
      await redisClient.del(`admin:verify:${verifyId}`);
      await redisClient.del(`admin:verify:attempts:${verifyId}`);
      res.status(429).send("Too many attempts. Start over from the picker.");
      return;
    }

    if (submittedCode !== payload.code) {
      await redisClient.incr(`admin:verify:attempts:${verifyId}`);
      const acct = await PlayerTesterModel.findOne({ _id: new Types.ObjectId(payload.accountId) }).lean() as any;
      res.send(accountVerifyTemplate({
        verifyId,
        returnTo: payload.returnTo,
        accountName: acct?.name || "Unknown",
        steamId: acct?.steamId || "",
        error: `Wrong code. ${5 - attempts - 1} attempts remaining.`,
      }));
      return;
    }

    // ✓ Code correct. One-time-use — delete immediately.
    await redisClient.del(`admin:verify:${verifyId}`);
    await redisClient.del(`admin:verify:attempts:${verifyId}`);

    res.cookie(ADMIN_COOKIE, signAdminCookie(payload.accountId, ip), {
      maxAge: ADMIN_COOKIE_TTL * 1000,
      httpOnly: true,
      sameSite: "lax",
    });

    logger.info(`${logPrefix} Admin verify SUCCESS: account ${payload.accountId} verified from IP ${ip} → ${payload.returnTo}`);
    res.redirect(payload.returnTo);
  } catch (e) {
    logger.error(`${logPrefix} Error in /account/verify: ${e}`);
    res.status(500).send("Verification error");
  }
});

app.get("/custom", async (req, res) => {
  try {
    const html = customLobbyTemplate({});
    res.send(html);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /custom: ${e}`);
    res.status(500).send("Error loading custom lobby page");
  }
});

app.get("/api/custom/whoami", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.json({ error: "Not connected to game" }); return; }
    res.json({ playerId: player.id, username: player.username });
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/whoami: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/create", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game. Launch the game first." }); return; }
    const result = await createLobby(player.id, player.username, player.ip);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/create: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/join", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game. Launch the game first." }); return; }
    const lobbyCode = req.body?.lobbyCode;
    if (!lobbyCode) { res.status(400).json({ error: "Lobby code is required." }); return; }
    const result = await joinLobby(lobbyCode, player.id, player.username, player.ip);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/join: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/leave", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await leaveLobby(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/leave: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/switch-team", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await switchTeam(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/switch-team: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/spectate", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await moveToSpectator(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/spectate: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/unspectate", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await moveToPlayer(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/unspectate: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/ready", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await toggleReady(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/ready: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/kick", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const targetId = req.body?.targetId;
    if (!targetId) { res.status(400).json({ error: "targetId required." }); return; }
    const result = await kickPlayer(player.id, targetId);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/kick: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/select-map", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const maps = Array.isArray(req.body?.maps) ? req.body.maps : [];
    const result = await setMapPool(player.id, maps);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/select-map: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/select-mode", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const mode = req.body?.mode;
    if (mode !== "1v1" && mode !== "2v2") { res.status(400).json({ error: "Invalid mode. Must be '1v1' or '2v2'." }); return; }
    const result = await selectMode(player.id, mode);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/select-mode: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.post("/api/custom/start", async (req, res) => {
  try {
    const player = await getPlayerFromReq(req);
    if (!player) { res.status(401).json({ error: "Not connected to game." }); return; }
    const result = await startMatch(player.id);
    res.json(result);
  } catch (e) { logger.error(`${logPrefix} Error in /api/custom/start: ${e}`); res.status(500).json({ error: "Internal error" }); }
});

app.get("/api/custom/status/:lobbyCode", async (req, res) => {
  try {
    const lobbyCode = req.params.lobbyCode.toUpperCase();
    const status = await getLobbyWithStatus(lobbyCode);
    if (!status) {
      res.status(404).json({ error: "Lobby not found." });
      return;
    }
    res.json(status);
  } catch (e) {
    logger.error(`${logPrefix} Error in GET /api/custom/status: ${e}`);
    res.status(500).json({ error: "Error fetching lobby status" });
  }
});

app.get("/api/custom/maps/:mode", async (req, res) => {
  const mode = req.params.mode;
  const maps = getMapList(mode);
  res.json({ maps });
});

// SSE endpoint for live lobby updates
app.get("/api/custom/events/:lobbyCode", async (req, res) => {
  const lobbyCode = req.params.lobbyCode.toUpperCase();

  // Set SSE headers
  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache",
    "Connection": "keep-alive",
  });

  // Create a dedicated Redis subscriber for this connection
  const sub = redisClient.duplicate();
  await sub.connect();

  const channelName = `custom_lobby_update:${lobbyCode}`;
  await sub.subscribe(channelName, (message: string) => {
    res.write(`event: lobby-update\ndata: ${message}\n\n`);
  });

  // Send initial state
  const initialState = await getLobbyWithStatus(lobbyCode);
  if (initialState) {
    res.write(`event: lobby-update\ndata: ${JSON.stringify(initialState)}\n\n`);
  }

  // Heartbeat to keep connection alive
  const heartbeat = setInterval(() => {
    res.write(`:heartbeat\n\n`);
  }, 15000);

  // Periodic connection status refresh (every 5 seconds)
  const statusRefresh = setInterval(async () => {
    try {
      const status = await getLobbyWithStatus(lobbyCode);
      if (status) {
        res.write(`event: lobby-update\ndata: ${JSON.stringify(status)}\n\n`);
      }
    } catch {
      // ignore
    }
  }, 5000);

  // Cleanup on disconnect
  req.on("close", () => {
    clearInterval(heartbeat);
    clearInterval(statusRefresh);
    sub.unsubscribe(channelName);
    sub.quit();
  });
});

app.post("/api/testing/deploy-rollback-server", async (req, res) => {
  await handleDeployRollbackServer(req, res);
});

app.post("/api/testing/destroy-rollback-server", async (req, res) => {
  await handleDestroyRollbackServer(req, res);
});

// ============================================================================
// AccelByte IAM Fake Endpoints (MUST be BEFORE hydraTokenMiddleware)
//
// The game's AccelByte SDK hits these HTTPS endpoints during initialization.
// We return minimal valid responses so the SDK proceeds to connect to the
// Lobby WebSocket at /lobby/ — which is our actual target for partyMemberJoinNotif.
//
// The game uses DNS redirect (accelbyte.io → our IP) so these requests arrive
// here on port 443 (HTTPS). They also work on port 8000 (HTTP) as fallback.
// ============================================================================

// OAuth2 token endpoint — client credentials or platform login
app.post("/iam/v3/oauth/token", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] POST /iam/v3/oauth/token — grant_type=${req.body?.grant_type || "unknown"}`);
  res.json({
    access_token: "ovs_fake_token_" + Date.now(),
    token_type: "Bearer",
    expires_in: 86400,
    refresh_token: "ovs_fake_refresh_" + Date.now(),
    refresh_expires_in: 86400 * 30,
    namespace: "multiversus",
    display_name: "OpenVersusPlayer",
    user_id: "ovs_fake_user_" + Date.now(),
    platform_id: "",
    platform_user_id: "",
    jflgs: 0,
    is_comply: true,
  });
});

// Platform-specific token exchange (Steam, Epic, etc.)
app.post("/iam/v3/oauth/platforms/:platform/token", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] POST /iam/v3/oauth/platforms/${req.params.platform}/token`);
  res.json({
    access_token: "ovs_fake_platform_token_" + Date.now(),
    token_type: "Bearer",
    expires_in: 86400,
    refresh_token: "ovs_fake_refresh_" + Date.now(),
    refresh_expires_in: 86400 * 30,
    namespace: "multiversus",
    display_name: "OpenVersusPlayer",
    user_id: "ovs_fake_user_" + Date.now(),
    platform_id: req.params.platform,
    platform_user_id: "",
    jflgs: 0,
    is_comply: true,
  });
});

// Token verification/revocation
app.post("/iam/v3/oauth/revoke", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] POST /iam/v3/oauth/revoke`);
  res.status(200).send();
});

app.post("/iam/v3/oauth/verify", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] POST /iam/v3/oauth/verify`);
  res.json({ exp: Math.floor(Date.now() / 1000) + 86400, namespace: "multiversus" });
});

// User info
app.get("/iam/v3/public/users/me", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] GET /iam/v3/public/users/me`);
  res.json({
    userId: "ovs_fake_user",
    displayName: "OpenVersusPlayer",
    namespace: "multiversus",
    emailVerified: true,
    platformId: "steam",
    country: "US",
  });
});

// Namespace config (SDK might check this during init)
app.get("/iam/v3/public/namespaces/:namespace", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-IAM] GET /iam/v3/public/namespaces/${req.params.namespace}`);
  res.json({
    namespace: req.params.namespace,
    displayName: "MultiVersus",
    status: "ACTIVE",
  });
});

// AccelByte Basic service — input validation config
app.get("/basic/v1/public/namespaces/:namespace/misc/input/validation", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-Basic] GET input validation config`);
  res.json({ data: [] });
});

// AccelByte config/discovery endpoints the SDK might check
app.get("/agreement/public/policies/namespaces/:namespace", (req, res) => {
  logger.info(`${logPrefix} [AccelByte-Agreement] GET policies for ${req.params.namespace}`);
  res.json([]);
});

// ============================================================================

app.use(hydraDecoderMiddleware);
app.use(hydraTokenMiddleware);

// New friends/search/accounts routes — BEFORE old router for priority
import { friendsRouter } from "./modules/friends/friends.routes";
import { fromBase64 } from "bytebuffer";
app.use(friendsRouter);

app.use(router);
app.use(sscRouter);

// Catch-all for AccelByte endpoints — AFTER router
app.all("/iam/*", (req, res) => {
  logger.warn(`${logPrefix} [AccelByte-IAM] UNHANDLED: ${req.method} ${req.url}`);
  res.json({ status: "ok" });
});
app.all("/basic/*", (req, res) => {
  logger.warn(`${logPrefix} [AccelByte-Basic] UNHANDLED: ${req.method} ${req.url}`);
  res.json({ status: "ok" });
});
app.all("/platform/*", (req, res) => {
  logger.warn(`${logPrefix} [AccelByte-Platform] UNHANDLED: ${req.method} ${req.url}`);
  res.json({ status: "ok" });
});
app.all("/social/*", (req, res) => {
  logger.warn(`${logPrefix} [AccelByte-Social] UNHANDLED: ${req.method} ${req.url}`);
  res.json({ status: "ok" });
});
app.all("/lobby/*", (req, res) => {
  logger.warn(`${logPrefix} [AccelByte-Lobby] UNHANDLED HTTP: ${req.method} ${req.url}`);
  res.json({ status: "ok" });
});
app.get("/ssc/invoke/hiss_amalgamation", (req, res, next) => {
  logger.info(`${logPrefix} Missing Crc, sending fresh one`);
  res.send(generate_hiss());
});

// Notification endpoints — the game binary has /accounts/me/notifications/ paths
// The game may poll for pending notifications (e.g. party invites)
app.get("/accounts/me/notifications", async (req, res) => {
  logger.info(`${logPrefix} GET /accounts/me/notifications requested`);
  // Return empty notifications array — invites are delivered via WebSocket
  res.send({ notifications: [], total: 0 });
});

app.get("/accounts/me/notifications/bulk", async (req, res) => {
  logger.info(`${logPrefix} GET /accounts/me/notifications/bulk requested`);
  res.send({ notifications: [], total: 0 });
});

// Game requests gameplay config via HTTP as a fallback — already delivered via WebSocket
app.get("/ssc/invoke/load_gameplay_config", async (req, res) => {
  const matchId = req.query.MatchId as string;
  logger.info(`${logPrefix} GET /ssc/invoke/load_gameplay_config for MatchId=${matchId}`);
  // Config is already sent via WebSocket (handleSendGamePlayConfig), return empty success
  res.send({ body: {}, metadata: null, return_code: 200 });
});

// Game notifies server that a player is leaving a match — process as ELO loss for the leaver
app.put("/matches/:id/leave", async (req, res) => {
  const matchId = req.params.id;
  const account = AuthUtils.DecodeClientToken(req);
  const playerId = account?.id || (req as any).token?.id;

  logger.info(`${logPrefix} PUT /matches/${matchId}/leave — player ${playerId || "unknown"}`);

  if (playerId && matchId) {
    // Process the leave as a loss for the leaver's team (dedup prevents double processing)
    processMatchLeave(matchId, playerId).catch((e) =>
      logger.error(`${logPrefix} Error processing match leave ELO: ${e}`)
    );
  }

  res.send({ body: {}, metadata: null, return_code: 200 });
});

// Leaderboard show endpoint — game fetches top 100 for ranked display
app.get("/leaderboards/:slug/show", async (req, res) => {
  const slug = req.params.slug;
  const count = parseInt(req.query.count as string) || 100;
  logger.info(`${logPrefix} GET /leaderboards/${slug}/show (count=${count})`);
  logger.info(`${logPrefix} Leaderboard show query: ${JSON.stringify(req.query)}`);
  logger.info(`${logPrefix} Leaderboard show headers: content-type=${req.headers["content-type"]}, accept=${req.headers["accept"]}`);

  try {
    // Parse mode from slug: "ranked_season5_1v1_all" → "1v1"
    const mode = slug.includes("1v1") ? "1v1" : slug.includes("2v2") ? "2v2" : "1v1";
    const leaderboard = await getLeaderboard(mode as "1v1" | "2v2", count);

    // Resolve each player's character from Redis
    const entries = await Promise.all(leaderboard.map(async (entry) => {
      let character = "character_wonder_woman";
      try {
        const conn = await redisClient.hGetAll(`connections:${entry.account_id}`);
        if (conn?.character) character = conn.character;
      } catch {}

      return {
        Identity: entry.account_id,
        Rank: entry.rank,
        Value: { _hydra_double: entry.elo },
        CharacterSlug: character,
      };
    }));

    logger.info(`${logPrefix} Leaderboard show response: ${entries.length} entries`);
    // DIAGNOSTIC: send garbage to see if game crashes/errors
    res.send("THIS IS NOT VALID DATA");
  } catch (e) {
    logger.error(`${logPrefix} Error in leaderboard show: ${e}`);
    res.json([]);
  }
});

// Leaderboard "around me" — shows the player's position with nearby players
app.get("/leaderboards/:slug/around/:playerId", async (req, res) => {
  const slug = req.params.slug;
  const playerId = req.params.playerId;
  const count = parseInt(req.query.count as string) || 4;
  logger.info(`${logPrefix} GET /leaderboards/${slug}/around/${playerId} (count=${count})`);

  try {
    const mode = slug.includes("1v1") ? "1v1" : slug.includes("2v2") ? "2v2" : "1v1";
    const playerRank = await getPlayerRank(playerId, mode as "1v1" | "2v2");

    if (!playerRank) {
      res.json([]);
      return;
    }

    // Get a window of players around this player's rank
    const leaderboard = await getLeaderboard(mode as "1v1" | "2v2", 200);
    const playerIndex = leaderboard.findIndex((e) => e.account_id === playerId);
    const half = Math.floor(count / 2);
    const start = Math.max(0, playerIndex - half);
    const end = Math.min(leaderboard.length, start + count + 1);
    const window = leaderboard.slice(start, end);

    const entries = await Promise.all(window.map(async (entry) => {
      let character = "character_wonder_woman";
      try {
        const conn = await redisClient.hGetAll(`connections:${entry.account_id}`);
        if (conn?.character) character = conn.character;
      } catch {}

      return {
        updated_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
        created_at: { _hydra_unix_date: Math.floor(Date.now() / 1000) },
        deleted: false,
        orphaned: false,
        orphaned_reason: null,
        public_id: entry.account_id,
        "identity.avatar": "",
        "identity.default_username": true,
        "identity.alternate.wb_network": [{ id: entry.account_id, username: entry.username, avatar: null }],
        "identity.alternate.steam": [{ id: entry.account_id, username: entry.username, avatar: null }],
        "wb_account.completed": true,
        "wb_account.email_verified": true,
        points: 0,
        state: "normal",
        wbplay_data_synced: false,
        wbplay_identity: null,
        locale: "en-US",
        "data.LastLoginPlatform": "EPlatform::PC",
        "data.__unused": null,
        [`server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.${mode}.BestCharacter.CharacterSlug`]: character,
        [`server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.${mode}.BestCharacter.CurrentPoints`]: entry.elo,
        [`server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.${mode}.BestCharacter.MaxPoints`]: entry.elo,
        [`server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.${mode}.BestCharacter.GamesPlayed`]: entry.wins + entry.losses,
        [`server_data.SeasonalData.Season:SeasonFive.Ranked.DataByMode.${mode}.BestCharacter.SetsPlayed`]: entry.wins + entry.losses,
        id: entry.account_id,
        "identity.username": entry.username,
        connections: [],
        rank: entry.rank,
        score: entry.elo,
        value: entry.elo,
      };
    }));

    res.json(entries);
  } catch (e) {
    logger.error(`${logPrefix} Error in leaderboard around: ${e}`);
    res.json([]);
  }
});

// Game submits/fetches player rank scores for leaderboard display during ranked matches
app.put("/leaderboards/bulk/score-and-rank/:playerId", async (req, res) => {
  const playerId = req.params.playerId;
  logger.info(`${logPrefix} PUT /leaderboards/bulk/score-and-rank/${playerId}`);
  try {
    const rank1v1 = await getPlayerRank(playerId, "1v1");
    const rank2v2 = await getPlayerRank(playerId, "2v2");
    res.send({
      body: {
        "1v1": rank1v1 ? { score: rank1v1.elo, rank: rank1v1.rank } : { score: 1000, rank: 0 },
        "2v2": rank2v2 ? { score: rank2v2.elo, rank: rank2v2.rank } : { score: 1000, rank: 0 },
      },
      metadata: null,
      return_code: 200,
    });
  } catch (e) {
    logger.error(`${logPrefix} Error in leaderboards/bulk/score-and-rank: ${e}`);
    res.send({ body: {}, metadata: null, return_code: 200 });
  }
});

app.use((req, res, next) => {
  // Suppress noisy polling endpoints from logs
  if (!req.url.includes('/ovs/notifications') && !req.url.includes('/ovs/my-friends')) {
    logger.info(`${logPrefix} NOT IMPLEMENTED - ${req.method} ${req.url}\n`);
    KitchenSink.TryInspectRequestVerbose(req);
  }

  res.send({ body: { Crc: getCurrentCRC(), MatchmakingCrc: MATCHMAKING_CRC }, metadata: null, return_code: 200 });
});

export const MVSHTTPServer = http.createServer(app);

// HTTPS server for AccelByte SDK (port 443)
// The game's AccelByte SDK connects via HTTPS to IAM endpoints and WSS to /lobby/.
// DNS redirect (accelbyte.io → our IP) routes these requests here.
let MVSHTTPSServer: https.Server | null = null;

function createHTTPSServer(): https.Server | null {
  const certDir = path.join(__dirname, "..", "certs");
  const certPath = path.join(certDir, "server-cert.pem");
  const keyPath = path.join(certDir, "server-key.pem");

  if (!fs.existsSync(certPath) || !fs.existsSync(keyPath)) {
    logger.warn(`${logPrefix} TLS certs not found at ${certDir} — HTTPS/WSS on port 443 will be DISABLED`);
    logger.warn(`${logPrefix} AccelByte Lobby WS (partyMemberJoinNotif) will not work without HTTPS`);
    return null;
  }

  try {
    const options: https.ServerOptions = {
      cert: fs.readFileSync(certPath),
      key: fs.readFileSync(keyPath),
    };
    const server = https.createServer(options, app);
    logger.info(`${logPrefix} HTTPS server created with certs from ${certDir}`);
    return server;
  } catch (e) {
    logger.error(`${logPrefix} Failed to create HTTPS server: ${e}`);
    return null;
  }
}

export async function start() {
  await connect();
  await loadAssets();
  await LoadConfig();

  // Flush stale online players — all WS connections died on restart,
  // so nobody is actually online yet. They'll re-add on reconnect.
  await redisClient.del("online_players");

  if (USE_INTERNAL_ROLLBACK) {
    if (!USE_INTERNAL_ROLLBACK_CPP) {
      const rollbackModule = await import("./game/udp.js");
      const rollbackServer = new rollbackModule.RollbackServer(env.UDP_PORT || 41234, 2);
    }
  }

  MVSHTTPServer.listen(port, "0.0.0.0", () => {
    logger.info(`${logPrefix} OVS Server running on ${port}`);
  });

  // Initialize AccelByte Lobby WebSocket service on the HTTP server (port 8000)
  const lobbyWs = initAccelByteLobbyWs(MVSHTTPServer);
  logger.info(`${logPrefix} AccelByte Lobby WebSocket initialized on HTTP server (port ${port})`);

  // Start HTTPS server on port 443 for AccelByte SDK (IAM + Lobby WSS)
  MVSHTTPSServer = createHTTPSServer();
  if (MVSHTTPSServer) {
    MVSHTTPSServer.listen(443, "0.0.0.0", () => {
      logger.info(`${logPrefix} HTTPS server running on port 443 (AccelByte IAM + Lobby WSS)`);
    });

    // Attach AccelByte Lobby WS upgrade handler to HTTPS server too
    // This way /lobby/ WebSocket upgrades work on both HTTP (8000) and HTTPS (443)
    lobbyWs.attachToServer(MVSHTTPSServer);
    logger.info(`${logPrefix} AccelByte Lobby WebSocket attached to HTTPS server (port 443)`);

    MVSHTTPSServer.on("error", (err: NodeJS.ErrnoException) => {
      if (err.code === "EADDRINUSE") {
        logger.error(`${logPrefix} Port 443 already in use — HTTPS disabled. Kill the process using port 443.`);
      } else if (err.code === "EACCES") {
        logger.error(`${logPrefix} Port 443 access denied — run as administrator or use a non-privileged port.`);
      } else {
        logger.error(`${logPrefix} HTTPS server error: ${err.message}`);
      }
    });
  }
}