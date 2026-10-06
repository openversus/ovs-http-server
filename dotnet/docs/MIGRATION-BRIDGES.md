# Migration bridges

Everything here ties the C# services to the TS server **temporarily**, so routes can move over one at a time while
the game keeps working. None of it is meant to survive the migration. Each entry says what it does and exactly when it
gets deleted. When the condition is met, delete the code *and* the entry.

Anything that is a bridge logs a `MIGRATION BRIDGE` warning at startup naming this file, so a running bridge is never
invisible. If a new bridge is added, it gets an entry here and that warning, or it doesn't get merged.

## Active

### 1. The proxy (`src/OpenVersus.Server.Proxy`)

- **What:** the game talks to the proxy. Routes listed in `Proxy:PortedRoutes` go to the C# http service; everything
  else goes to the TS server (`Proxy:TsUrl`).
- **Why:** each route is tried against the game as soon as it is ported.
- **Delete when:** every route the game uses is ported. Then the game talks to the C# http service directly, and the
  proxy project, its tests and `KnownServices.Proxy` go.

### 2. Shared data contracts (Redis keys, Mongo collections)

- **What:** the C# services read and write the same Redis keys and Mongo documents as the TS services, exactly as they
  do (the "migration contract" comments at the top of `AccessService`, `FriendsService`, `OpsService`, ...). The C# side copies TS and
  mongoose quirks on purpose (field order, `__v`, timestamps, defaults written into old documents).
- **Why:** TS services (websocket, matchmaking, the website) still read what C# writes, and the other way round.
- **Pub/sub channels too:** a message C# publishes is read by the TS websocket, which then tells the game. So far:
  `client_update:modal` (`ClientUpdateGate`, show a player the update
  toast: `{playerId, nonce}`), `matchmaking:cancel` (`PartyService`, someone joined a party: `{playersIds,
  matchmakingId: "party-changed"}`; `MatchmakingRequestService`, the game's cancel: `{playersIds, matchmakingId}`; the TS
  websocket keeps the queue ticket and its tick in memory, so it cancels them) and `party:queued`
  (`MatchmakingRequestService`, the matchmaking request's ticket, as queueMatch builds it; the TS websocket sends the game
  OnMatchmakerStarted and pushes the ticket onto the `1v1` or `2v2` list, which the matchmaker reads),
  `perks:notifications` (`PerksLock`, every player of a match has locked their perks: `{containerMatchId, playerIds}`;
  the TS websocket puts the perks into each player's match config, which it holds in memory, and sends it again), and the rollback callbacks' two (`RollbackCallbacks`):
  `game_server_ready:notifications` (`{containerMatchId, playerIds, resultId, rollbackPort}`: the TS websocket sends each
  player `game-server-instance-ready`, with 127.0.0.1 and their node's port in a P2P match) and `match:end`
  (`{playersIds, matchId}`: the TS websocket's `handleOnMatchEnd`, the match's end on its side; the C# match end,
  `MatchEnd`, is built and off: `MatchEnd:Enabled`, on only where no TS websocket holds the players).
  Their payloads are JSON exactly as the TS server writes them. The party routes' other messages to players
  (`OnLobbyModeUpdated`, `InviteReceivedForLobby`, `PlayerJoinedLobby`, `PlayerLeftLobby`, `PlayerReadyForLobby`,
  `OnPlayerLoadoutLocked`) are built in C# and go through `ws:send` (4), as the TS websocket would have built them; so
  are the custom lobby's (`CustomLobbyService`), which the TS server published on `custom_lobby:notification` for its
  websocket to relay. That channel is now published only by the TS websocket itself (a player who disconnects leaves
  their lobby) and goes with it. The same since slice 3b for the channels whose TS handler only built a message: a
  lobby join's three messages (`PartyLobbyService`, was `lobby:player_joined`), `matchmaking-complete` (`MatchLauncher`,
  `MatchmakingWorker`, was `matchmaking:complete`), a toast (`MatchToasts` grants the toastee their 2 and shows it, was
  `toast:received`), a ranked set's check-in, leaver and ranks (`RankedSets`, and the ranks after a pre-game dodge,
  `MatchStatusEvents`: was `ranked_set:checkin`, `ranked_set:leaver`, `ranked_set:fullrankupdate`). The TS server's
  own routes that still publish those channels (a TS-run custom lobby's rematch start, the TS websocket's own pregame
  dodge) are answered by its websocket as before.
- **Starting a match:** `IMatchLauncher` (`Core/Matches/`, for rifts and custom lobbies) writes what the TS custom lobby
  writes when a match starts (`match:{id}`, `match:{id}:perks:{bot}`, the notification at `{id}`, `rollback:current_port`
  on demand), publishes `match:notifications` (the TS `MATCH_FOUND_NOTIFICATION`, with a custom game's settings and
  its spectators; with `Realtime:Gateway` on, appends it to `match:launched` instead: bridge 9) and sends its players
  `matchmaking-complete` (ws:send; with the switch on, the match flow sends it once the config is built). The TS
  websocket sends the match to the game, and the match flow's rollback routes (`RollbackCallbacks`) serve it. For modes the websocket does not know,
  the notification carries two fields added to the TS type for this (`src/config/redis.ts`): `gameplayConfigOverride`
  and `playerConfigOverrides`, merged over the websocket's PvP gameplay config in `handleSendGamePlayConfig`, and
  `gameplayConfigTemplate` / `gameplayConfigData` (the config sent under another template, with fields beside it: a
  rift retry's `RiftRetryNotification` and `PriorMatchId`). Delete
  them with the websocket's port.
- **Lobby keys:** `lobby:{id}` (JSON written by the TS `ssc.ts` and `websocket.ts`; C# keeps every field it does not
  change as read), `player_lobby:{player}`, `pending_join_lobby:{player}`, `player:{player}:lobby:{id}`, `party_ready:{id}`
  and `player:{player}` `character`/`skin`/`ip`/`profileIcon` (the matchmaker reads `ip`): written by the party routes
  (`PartyService`) as the TS server writes them, TTLs included; `lobby_redirect:{id}` is read only. C# also creates rift
  lobbies (`RiftLobbyService`, `create_rift_lobby`): the same `lobby:{id}` JSON, `player_lobby:{player}`,
  `player:{player}:lobby:{id}` and `connections:{player}` `lobby_id` as the TS `create_party_lobby` writes, with a mode
  the TS server never writes (`"rift_lobby"`) and three more fields (`riftConfigSlug`, `chapterGuid`,
  `chapterDifficulty`) that TS code reading `lobby:{id}` (joins, invites) will see. The rift loadout lock writes
  `player:{player}` `character`/`skin` as the TS `lock_lobby_loadout` does.
- **Custom lobby keys** (`CustomLobbyService`): `custom_lobby_ssc:{lobby}` (the lobby's JSON, changed only by Lua
  scripts that started as the TS server's; the TS match end, rematch vote and websocket disconnect still read and write
  it), `ssc_custom_lobby_player:{player}`, `lobby_code:{code}`, `ssc_custom_lobby_match:{match}` (the TS match end and
  rematch read it) and `bot_config:{bot}` (the TS websocket builds a bot's match config from it), TTLs as the TS
  server's. The match end and the rematch are ported (`MatchEnd` in the match flow opens the vote; `Rematches` in
  lobbies answers `rematch_accept`/`rematch_decline` and runs its timer, with the TS keys
  `ssc_custom_lobby_rematch_timer:{lobby}` and `ssc_custom_lobby_rematch_accept:{lobby}`). The match end runs only with
  `MatchEnd:Enabled`; until then the TS websocket ends the match and opens the vote (those two keys, and its own 25 s
  timer in its process), and the C# routes count the accepts on it: the last accept starts the rematch from C# (the
  TS timer then finds its key gone), a decline ends it; with no decline and not every accept, the TS timer starts it.
- **Matchmaking** (`MatchmakingWorker` in its own executable, `OpenVersus.Server.Matchmaking`; on unless `Matchmaking:Enabled` is false): the queues `1v1` and `2v2` (ticket JSON lists the TS
  websocket fills when a party queues and empties on a cancel or disconnect), `player_heartbeats`,
  `player:{id}:blocked` and `player:{id}` `ip` are read as the TS worker reads them; a match writes what the TS worker
  writes (`match:{id}` with the tickets as queued, the notification at `{id}`, `ranked_set:{id}`,
  `player_ranked_set:{player}`; the set's two for 20 min where TS gave 10, which a game could outlast before the
  websocket wrote them again), publishes `match:notifications` and sends each ticket's players their `matchmaking-complete`
  (ws:send). Each
  queue is worked under the TS lock (`matchmaking:lock:{queue}`), so the C# and TS workers can run side by side. The
  maps it picks from are a copy of the TS `src/data/maps1v1.json` / `maps2v2.json` (`Matchmaking/maps.json`,
  `tools/matchmaking/gen_maps.mjs`): a map change goes to the TS files and is generated again (`--check` tells) until
  the TS worker and websocket (which reads those files for hazards) are gone.
- **Ranked sets** (`RankedSets`, `SetRatings`: the set routes between a set's games): `ranked_set:{set}`,
  `player_ranked_set:{player}`, `ranked_set_checkins:{set}`, `ranked_disconnect:{player}`, `match_server_crash:{match}`,
  `elo_processed_set:{set}` (the TS websocket skips a set it finds there), `match_to_set:{match}` and `match_characters:{match}`,
  as the TS routes read and write them; the TS websocket still writes the set's score at each game's end and rates a set
  won at a game's end (6). The next game is written as the TS `createNextSetMatch` wrote it (`match:{id}` with one ticket
  of every player, the notification at `{id}`, `match:notifications`), except that the set's keys written with it
  (`ranked_set`, `player_ranked_set`, `match_to_set`) live 20 min where TS gave 10. `ranked_set_match:{set}` (the set's
  current game) is C#'s alone. Ratings and set stats (`eloratings`, `playerstats`) keep mongoose's shape (int32 counts, `updated_at` a
  double, the upsert's `$setOnInsert` defaults): `tools/matches/set_diff.mjs`.
- **Cosmetics:** `player:{id}:cosmetics` (JSON, no TTL) and the `cosmetics` collection, read by
  `get_equipped_cosmetics` and written by the six equip routes (`CosmeticsService`): the stored document as
  `JSON.stringify` writes a lean read (`_id`, `account_id`, `__v` kept), with a taunt entry per character. The TS
  websocket and match handlers (`websocket.ts`, `handlers/matches.ts`, `ssc/ssc.ts`) read the key to show a player's
  cosmetics to the others in a match. They read `connections:{id}:cosmetics` first (a hash, a field per key, each
  JSON-encoded; deleted by the TS websocket at disconnect): the C# party routes make it (`create_party_lobby`, a party
  join and rejoin, `lock_lobby_loadout`: `ICosmeticsService.WriteMatchCopyAsync`), as the TS ones did, and the C# equips
  refresh it when it exists and never create it (TS equips leave it stale until the game restarts). The TS match flow
  still makes it too. `set_profile_icon` also writes `playertesters.profile_icon`, which `/access`,
  profiles and friends read. The Mongo writes keep mongoose's upsert shape (`$setOnInsert` with `__v` and the schema's
  defaults).
- **Rewards** (`RewardTracks/RewardGrants.cs`): `playercounters` `match_toasts` gets an atomic `$inc` for toast rewards,
  as the TS `adjustMatchToasts` writes it (the TS websocket reads and moves the same counter: toasts given and
  received, the daily bonus). Everything else a reward pays is recorded in `playeritems` (C# only), which the C#
  inventory answer reads.
- **GameplayPreferences** (the player's input settings: deadzones, input buffer, item pickup; they change how a match
  feels): `playertesters.GameplayPreferences` (an int32) and `connections:{id}` `GameplayPreferences` (text), written by
  both servers with the same rules (C# `Core/Preferences`, TS `utils/gameplayPreferences.ts` and
  `services/gameplayPreferences.ts`): only a whole number is stored, never the default 964 over a player's value, and
  0 is a value. The game sends its current value with every party-lobby request; both servers record it before the
  handler (C# `GameplayPreferencesRecorder`, TS `recordGameplayPreferencesFromRequest`), and every lobby and match
  reads the stored one. The TS websocket builds the match configs from `connections:{id}`.
- **Asset sync:** `dataassets` and the `config` collection's `CRC` are written only by the TS server's `POST /syncAsset`
  (`dataAssetSync.ts`: the asset, then the CRC bumped). C# reads both (`HissService` builds its answer once per CRC;
  the inventory, cosmetics and hiss answers read the assets), so an asset sync goes through the TS server until
  `/syncAsset` is ported, and must keep bumping the CRC.
- **Delete when:** no TS service reads or writes that key, collection or channel any more. Then the C# side may change
  the shape, drop the mongoose quirks, and the contract comment goes.

### 3. `/batch` sends the sub-requests C# has not ported to TS (`src/OpenVersus.Server.Http/Batch/BatchRunner.cs`)

- **Decided:** 2026-09-29 (option 1, on the condition that it is recorded here and never becomes permanent).
- **Not part of the bridge:** a sub-request another C# service owns (`owner` in `routes.json`) goes to that service
  through the router (`Batch:EdgeUrl`: the proxy on the bench, the router in production) as a request of its own, with
  the same headers. That is how a batch spans services, and it stays when TS is gone.
- **What:** the C# `/batch` runs each of its own service's sub-requests through its own pipeline. Those that reach a stub (not ported), and
  those whose route is listed in `Batch:ForwardRoutes`, go to the TS server (`Batch:TsUrl`) together, as one TS `/batch`
  carrying the game's batch headers as they came (plus `X-Real-IP`), so the TS server runs them exactly as it runs its
  own batches. Its answers are put into the response byte for byte, in the game's order.
- **Headers, as in TS:** a sub-request answered in C# gets its own headers, the batch's `x-hydra-access-token`, the Hydra
  content type and the batch's client address. The TS `/batch` gives its sub-requests the same (the token is copied, the
  client address is inherited); the batch's other headers (`x-steam-id`, `x-install-id`, `X-OVS-Identity`, ...) reach
  neither server's sub-requests.
- **Rolling a route back:** taking a route out of `Proxy:PortedRoutes` does not reach into batches; listing it in
  `Batch:ForwardRoutes` (same `METHOD /path` form) does. Both are live settings.
- **Why:** the login's two batches hold about 15 SSC and config reads; this lets each one move to C# on its own.
- **Delete when:** every route a batch can contain is ported (the SSC catch-all `SscUnlisted` included). Then the
  forwarding code, `Batch:TsUrl`, `Batch:ForwardRoutes`, `Batch:ForwardTimeoutSeconds`, the startup warning and this
  entry go; `/batch` keeps running its sub-requests in C#.

### 4. Rift and mission progress from match results reach the game through the TS websocket

- **Decided:** 2026-09-30, on the condition that it is recorded here: each piece has to be ported to C#.
- **What:** the results themselves are C#'s now: the http service's `submit_end_of_match_stats` (`MatchResults`)
  appends each report to the stream `match:results`, and the match flow (`MatchResultStream`) records missions, match
  XP, rift progress and the match's stats from it (see "Not bridges"). What is left is telling the game: the rift
  progress (`RiftProgressService`: `OnLobbyRuntimeDataUpdated`, `OnLobbyRiftStateUpdated`) and the missions
  (`MissionProgress`: `MissionUpdatesComplete`, a `profile-notification`) are published on `ws:send` (`{playerIds,
  message}`), a generic channel the TS websocket (`src/websocket.ts`) answers by sending `message`, as it is, to each
  connected player named. `MatchResultStream` logs a `MIGRATION BRIDGE` warning at startup for this.
- **Why:** the client never asks for its rift progress; the server works it out from the match result and pushes it.
  Only the TS websocket reaches the game.
- **Delete when:** the websocket is ported (`ws:send` is then answered by the C# gateway).

### 5. `ovsctl player disconnect` closes the connection through the TS websocket

- **What:** the control API (`POST /control/ops/players/{who}/disconnect`, `OpsService.DisconnectPlayerAsync`)
  publishes `ws:disconnect` (`{playerId}`); the TS websocket (`src/websocket.ts`) closes that player's socket with
  `terminate()`, the path its heartbeat timeout takes, so the usual cleanup runs and the game logs out. The command
  refuses when no websocket service is subscribed (the publish reached nobody). Every C# service logs a
  `MIGRATION BRIDGE` warning for it at startup (each has the control API).
- **Why:** a client stuck on an unanswered call has no way out of the menu; this frees it without restarting the game
  or the websocket service.
- **Delete when:** the realtime gateway holds the players (the bench switch): it already answers `ws:disconnect` itself
  (`OpenVersus.Server.Realtime`, with a connection id or an exception when given), so the channel stays as the gateway's;
  the TS handler and the startup warning go.

### 6. Ratings (ELO) are still partly the TS server's, which asks "does this match count" its own ways

- **What:** a set ended between its games is rated in C# (`RankedSets`: the set over at its check-ins, a concede, an
  opponent who disconnected and stayed offline; `SetRatings`, the TS `processSetResult` and `recordSetStats` value for
  value), and only when `RatedMatches` says the set counts: the regular 1v1/2v2 queues, no bot, not a password match, not
  a custom game. So is a pre-game dodge the rollback server reports (`MatchStatusEvents`, the TS `handlers/match_status.ts`;
  unrated, it does the rest all the same). The TS server still rates in three places: a set won at a game's end (the
  websocket's `handleOnMatchEnd`, with the set's `elo_processed_set` key between the two so a set is rated once), a
  pre-game dodge or a disconnect the websocket sees (`websocket.ts`; the same `elo_processed` and `elo_processed_set` keys
  as `MatchStatusEvents`, so whichever comes first decides, and a rift dodge the websocket sees first is still rated), and
  a player leaving a match (`PUT /matches/:id/leave`, `processMatchLeave` then `processMatchResult`). The leave skips a
  match whose `match:{id}` has `isPasswordMatch`; the websocket's paths skip only a match config with `isCustomGame`, and
  none of them leaves bots out. Every match C# starts
  (`MatchLauncher`: rift nodes, custom lobbies; the Casual queue's matches too) has `isPasswordMatch` and a mode of 1v1 or
  2v2; a custom lobby's and the Casual queue's (human or bots: `BotDefaults.UnrankedNotificationFields`) also have
  `isCustomGame`, which keeps them out of all of them and out of the TS best-of-3 sets. A rift match has not: a player who
  leaves one before or during it is charged on their regular 1v1/2v2 rating, and the bot gets a rating document. Read in
  the code, not seen yet: the bench's eloratings had no bot ids (2026-10-02). TS is not changed for it (it is going away).
- **Rule (for the port):** ratings count for the regular 1v1/2v2 queues only (ranked sets included); never rifts, custom
  lobbies or Casual (Casual may get a rating of its own, kept apart). Every C# rating path asks `RatedMatches`
  (`Core/Leaderboards`), and it never rates a bot.
- **Until then:** do not ship rift matches to prod before the TS paths are ported, or accept the leak there (giving rift
  matches `isCustomGame` would close it, untested: it also changes what the TS websocket does at a rift match's end).
- **Delete when:** match results (the set won at a game's end included), the websocket's dodges and disconnects, and the
  match leave are ported and ask `RatedMatches`.

### 7. A Casual match ends with no rematch: the TS websocket declines it for everyone

- **What:** a Casual queue match (people or bots) is unranked, marked `isCustomGame` for the TS websocket
  (`BotDefaults.UnrankedNotificationFields`). At an unranked match's end that is not a custom lobby's, the TS websocket
  (`handleOnMatchEnd`, websocket.ts) sends every player `RematchDeclinedNotification` one second later
  (`sendRematchDeclinedToPlayers`): the game shows the rematch option, which then disappears as if the opponent (a bot
  too) had declined, and everyone goes back to the menus. Seen on the bench, 2026-10-02.
- **What it must do (the requirement for the port):** unranked is single games with an optional rematch (no best-of-3
  set; that is ranked only). At a Casual match's end the rematch option stays up for its timer; a bot always accepts
  (at the latest when the timer runs out); when every player has accepted (`rematch_accept`, or the game's timer), a new
  single game starts against the same opponents (bots: the same fighters), never rated; any decline (`rematch_decline`)
  sends everyone back to the menus.
- **Why:** match end is still the TS websocket's; C# cannot stop its decline without changing TS, which is going away.
- **Built:** the C# match end opens a Casual match's vote (`MatchEnd`, `RematchVotes`), and lobbies takes the accepts and
  declines and starts the rematch (`Rematches`, with its timer), as above; on only with `MatchEnd:Enabled`, which stays
  off while the TS websocket holds the players.
- **Delete when:** the realtime gateway replaces the TS websocket and `MatchEnd:Enabled` is on.

### 8. P2P is switched in two places, and parts of a P2P match are still the TS server's

- **What:** whether eligible matches run P2P (on the players' own nodes) is `Rollback:P2P` for the matches C# starts
  (`MatchLauncher`: custom lobbies, the Casual queue, rift nodes; the C# matchmaker) and the TS server's `P2P_ROLLBACK`
  environment variable for the ones it still starts: a custom lobby's rematch, and its own matchmaker when it runs (a
  ranked set's next game is C#'s: `RankedSets`). Both write `p2p` into the match config with the same rule
  (`Matches/P2P.cs`, `src/services/nodePort.ts` hasP2PHost: every match with a human who plays). The match flow serves
  the nodes (`RollbackCallbacks`: `/ovs_register` signed and holding game-server-instance-ready, `/ovs_p2p_ready`,
  `/ovs_p2p_failed`, `/ovs_match_started`; `NodeConfig`: `/ovs_node_config`); the rest of a P2P match is TS:
  `/api/identify` (the node's port) and the websocket (sends the game to `127.0.0.1` and that port, P2P_NODE_PORT when
  the client reported none; with `Realtime:Gateway` on, the match flow sends it, `MatchLaunches`, Rollback:P2PNodePort). `Rollback:P2P` takes `P2P_ROLLBACK` when it is not set itself, but a cluster setting changed
  through the control API is not seen by TS: with the two different, a custom lobby's game and its rematch can disagree,
  and so can a set's game 1 when the TS matchmaker made it.
- **Until then:** change both together; each executable that starts matches logs the C# value once it has started (the
  cluster settings are loaded by then).
- **Delete when:** custom lobby rematches start in C# (`Rematches`, with `MatchEnd:Enabled`), `/api/identify` is ported,
  the TS matchmaker is retired, and the websocket reads `p2p` and the node port from C#'s config (the realtime gateway:
  `Realtime:Gateway`, slice 3c).

### 9. The match flow builds match configs from the TS websocket's channels, and the TS websocket still sends them

- **What:** `GameplayConfigBridge` (`Core/Matches/GameplayConfigs.cs`, in the match flow) subscribes to
  `match:notifications` and `perks:notifications`, the channels the TS websocket builds and sends each match's config
  from, and builds the same config (`GameplayConfigs`), kept per player in `match_config:{player}`, as
  `GameplayConfigs:Mode` says: `Off` (the default) nothing; `Shadow` the config only, writing nothing the TS server reads,
  so it can be compared with what the TS websocket sends while that still sends it; `On` also what the TS websocket
  writes beside it (`match_characters`, the cosmetics match copy, a missing rating). C# sends nothing: the game still gets
  its config from the TS websocket, whose copy lives in its memory. Subscribing catches every match, the ones the TS
  server still starts (a custom lobby's rematch) included; with more than one match flow replica, each builds the same
  config.
- **Until then:** `Off` by default; the bench runs `Shadow` while the C# config is compared with what TS sends.
  With `Realtime:Gateway` on (slice 3c), the launchers no longer publish `match:notifications`: they append each match to
  the stream `match:launched` (`MatchLaunches`), and the match flow (`MatchLaunchStream`) builds the config (`On`, whatever
  the setting says) and sends it, after each player's `GameServerReadyNotification` and each party's
  `matchmaking-complete`, through the gateway; a match whose config cannot be built is called off instead (nobody is told
  of it; a searching game is cancelled, any other closed after a banner). The bridge then
  hears no C# match; a match the TS server still starts (its custom lobby rematch timer, only reached with match end off,
  which the switch turns on) would get no C# config.
- **Delete when:** the realtime gateway replaces the TS websocket (slice 3e): the perks lock (`PerksLock`) calls
  `IGameplayConfigs` directly too (slice 3c), and the bridge and the setting go.

## Not bridges (kept after the migration)

- The stream `match:results` (decided 2026-10-02): the http service that receives a match report appends it
  (`MatchResults`, at most ~10,000 kept); the match flow replicas read it as one consumer group (`MatchResultStream`),
  so each result is handled once, a result appended while no replica runs waits instead of being lost, and a replica
  that dies leaves its unacknowledged results to the others (XAUTOCLAIM after a minute; Redis 6.2+). It replaced the
  pub/sub channel `match:end_of_match_stats` the TS server published.

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
