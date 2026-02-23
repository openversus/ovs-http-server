import { logger, logwrapper, BE_VERBOSE } from "./config/logger";
import express from "express";
import router from "./router";
import { hydraDecoderMiddleware } from "./middleware/hydraParser";
import * as https from "https";
import * as http from "http";
import * as fs from "fs";
import * as path from "path";
import { hydraTokenMiddleware } from "./middleware/auth";
import { connect } from "./database/client";
import { generate_hiss } from "./handlers/hiss_amalgation_get";
import { redisClient, redisGetMatchConfig, redisPublisdEndOfMatch, redisGetLobbyState, redisSaveLobbyState, redisGetPlayerConnectionByIp, redisSavePlayerLobby, redisPublishLobbyRejoin, RedisLobbyRejoinNotification, redisSavePartyKey, redisGetPartyKey, redisDeletePartyKey } from "./config/redis";
import { GAME_SERVER_PORT } from "./game/udp";
import { sscRouter } from "./ssc/routes";
import { getCurrentCRC, LoadConfig, MATCHMAKING_CRC } from "./data/config";
import { PlayerTester, PlayerTesterModel } from "./database/PlayerTester";
import { RegExpMatcher, TextCensor, englishDataset, englishRecommendedTransformers, asteriskCensorStrategy } from "obscenity";
import env from "./env/env";
import { syncRouter } from "./dataAssetSync";
import { loadAssets } from "./loadAssets";
import * as SharedTypes from "./types/shared-types";
import { isParameter } from "typescript";
import * as nodeutil from "node:util";
import * as KitchenSink from "./utils/garbagecan";
import * as AuthUtils from "./utils/auth";
import { AccountToken, IAccountToken } from "./types/AccountToken";
import { isNameBanned, isNameForceChange, stringContainsBannedName, stringContainsForceChangeName, banIP } from "./services/banService";
import { NameGenerator } from "./utils/namegeneration";
import { initAccelByteLobbyWs } from "./accelByteLobbyWs";

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
const stringIsOnlyWhitespace = (string: string): boolean => string.trim().length === 0;

process.on("warning", (e) => {
  logger.warn(`${logPrefix} ${e.stack}`);
});

app.use(express.json());
app.use(express.urlencoded());
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

app.get("/namechange", async (req, res) => {
  try {
    let ip = req.ip!.replace(/^::ffff:/, "");
    if (typeof ip !== "string") {
      res.status(400).send("Invalid IP");
      return;
    }
    let player = await PlayerTesterModel.findOne({ ip });
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
    let ip = req.ip!.replace(/^::ffff:/, "");
    let player = await PlayerTesterModel.findOne({ ip });
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

    // Upsert the player's name based on IP
    let filtered;
    if(!error) {
      const matches = matcher.getAllMatches(name);
      filtered = censor.applyTo(name, matches);
      await PlayerTesterModel.findOneAndUpdate({ ip }, { name: filtered.substring(0, 24).trim() }, { upsert: true, new: true });
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
    res.send("");
  }
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
    const ip = req.ip!.replace(/^::ffff:/, "");
    let player = await PlayerTesterModel.findOne({ ip });
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
    const ip = req.ip!.replace(/^::ffff:/, "");
    let player = await PlayerTesterModel.findOne({ ip });
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
    const conn = await redisGetPlayerConnectionByIp(ip);
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
    const ip = req.ip!.replace(/^::ffff:/, "");
    let player = await PlayerTesterModel.findOne({ ip });
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

app.use(hydraDecoderMiddleware);
app.use(hydraTokenMiddleware);

app.use(router);
app.use(sscRouter);
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

app.use((req, res, next) => {
  logger.info(`${logPrefix} NOT IMPLEMENTED - ${req.method} ${req.url}\n`);
  KitchenSink.TryInspectRequestVerbose(req);

  res.send({ body: { Crc: getCurrentCRC(), MatchmakingCrc: MATCHMAKING_CRC }, metadata: null, return_code: 200 });
});

export const MVSHTTPServer = http.createServer(app);
// export const MVSHTTPServer = https.createServer(options,app);

export async function start() {
  await connect();
  await loadAssets();
  await LoadConfig();

  if (USE_INTERNAL_ROLLBACK) {
    if (!USE_INTERNAL_ROLLBACK_CPP) {
      const rollbackModule = await import("./game/udp.js");
      const rollbackServer = new rollbackModule.RollbackServer(env.UDP_PORT || 41234, 2);
    }
  }

  MVSHTTPServer.listen(port, "0.0.0.0", () => {
    logger.info(`${logPrefix} OVS Server running on ${port}`);
  });

  // Initialize AccelByte Lobby WebSocket service on the same HTTP server
  // This handles WebSocket upgrades to /lobby/ for party invites, friends, etc.
  initAccelByteLobbyWs(MVSHTTPServer);
  logger.info(`${logPrefix} AccelByte Lobby WebSocket initialized on HTTP server (port ${port})`);
}
