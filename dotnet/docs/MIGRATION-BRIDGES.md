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
- **Pub/sub channels:** none is published for the TS websocket any more (slice 3e, the realtime gateway in its place):
  what its handlers did is done where the message is caused (`MatchmakingQueue` for `party:queued` and
  `matchmaking:cancel`, `MatchLaunches` for `match:notifications` and `matchmaking:complete`, `RollbackCallbacks` for
  `game_server_ready:notifications`, `MatchEnd` for `match:end`, `PerksLock` for `perks:notifications`,
  `ClientUpdateGate` for `client_update:modal`, and since slice 3b the lobby, toast and ranked set channels), and every
  message to a game goes through `ws:send`, which the realtime gateway delivers. The TS server's own routes that still
  run behind the proxy (bridge 1) publish their channels to nobody: no TS websocket runs.
- **Starting a match:** `IMatchLauncher` (`Core/Matches/`, for rifts and custom lobbies) writes what the TS custom lobby
  writes when a match starts (`match:{id}`, `match:{id}:perks:{bot}`, the notification at `{id}`, `rollback:current_port`
  on demand) and appends the notification (the TS `MATCH_FOUND_NOTIFICATION`, with a custom game's settings and its
  spectators) to `match:launched`, from which the match flow builds the config and tells the players (`MatchLaunches`:
  `GameServerReadyNotification`, `matchmaking-complete`, the config), and its rollback routes (`RollbackCallbacks`)
  serve it. For modes the websocket does not know,
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
  it; behind the realtime gateway a disconnect is C#'s, `LobbyDisconnects`), `ssc_custom_lobby_player:{player}`, `lobby_code:{code}`, `ssc_custom_lobby_match:{match}` (the TS match end and
  rematch read it) and `bot_config:{bot}` (the TS websocket builds a bot's match config from it), TTLs as the TS
  server's. The match end and the rematch are ported (`MatchEnd` in the match flow opens the vote; `Rematches` in
  lobbies answers `rematch_accept`/`rematch_decline` and runs its timer, with the TS keys
  `ssc_custom_lobby_rematch_timer:{lobby}` and `ssc_custom_lobby_rematch_accept:{lobby}`).
- **Matchmaking** (`MatchmakingWorker` in its own executable, `OpenVersus.Server.Matchmaking`; on unless `Matchmaking:Enabled` is false): the queues `1v1`, `2v2` and `FFA` (ticket JSON lists the TS
  websocket fills when a party queues and empties on a cancel or disconnect), `player_heartbeats`,
  `player:{id}:blocked` and `player:{id}` `ip` are read as the TS worker reads them; a match writes what the TS worker
  writes (`match:{id}` with the tickets as queued, the notification at `{id}`, `ranked_set:{id}`,
  `player_ranked_set:{player}`; the set's two for 20 min where TS gave 10, which a game could outlast before the
  websocket wrote them again), publishes `match:notifications` and sends each ticket's players their `matchmaking-complete`
  (ws:send). Each
  queue is worked under the TS lock (`matchmaking:lock:{queue}`), so the C# and TS workers can run side by side. The
  maps it picks from are a copy of the TS `src/data/maps1v1.json` / `maps2v2.json` (`Matchmaking/maps.json`,
  `tools/matchmaking/gen_maps.mjs`): a map change goes to the TS files and is generated again (`--check` tells) until
  the TS worker and websocket (which reads those files for hazards) are gone. An `FFA` match is mode `FFA` in the
  notification, from which the TS websocket sends it unranked as `evtq_ffa`; it has no ranked set. Outside the FFA
  window (`Ffa:WeekendOnly`, `Matchmaking/FfaSchedule.cs`) the matchmaker empties the `FFA` list and publishes
  `matchmaking:cancel` for each ticket, as the TS worker did. The TS website reads its own `FFA_WEEKEND_ONLY` (the FFA
  searching count, shown only while open), which `Ffa:WeekendOnly` takes when not set itself: keep the two the same.
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

### 6. Ratings (ELO): a player leaving a match is still rated by the TS server, which asks "does this match count" its own way

- **What:** every rating path that ran in the TS websocket is C#'s now (`RankedSets`, `MatchEnd`, `MatchStatusEvents`,
  `MatchDisconnects`; `SetRatings`, the TS `processSetResult` and `recordSetStats` value for value), and each asks
  `RatedMatches`: the regular 1v1/2v2 queues, no bot, not a password match, not a custom game. One TS path is left: a
  player leaving a match (`PUT /matches/:id/leave`, `processMatchLeave` then `processMatchResult`), which skips only a
  match whose `match:{id}` has `isPasswordMatch` and leaves bots in. Every match C# starts (`MatchLauncher`: rift nodes,
  custom lobbies, the Casual queue's) has `isPasswordMatch`, so none of them is rated there.
- **Rule (for the port):** ratings count for the regular 1v1/2v2 queues only (ranked sets included); never rifts, custom
  lobbies or Casual (Casual may get a rating of its own, kept apart). Every C# rating path asks `RatedMatches`
  (`Core/Leaderboards`), and it never rates a bot.
- **Delete when:** the match leave is ported and asks `RatedMatches`.

### 8. P2P is switched in two places, and a P2P node's port is still the TS server's

- **What:** whether eligible matches run P2P (on the players' own nodes) is `Rollback:P2P` for every match C# starts
  (`MatchLauncher`: custom lobbies and their rematches, the Casual queue, rift nodes; the C# matchmaker; a ranked set's
  next game, `RankedSets`), and the TS server's `P2P_ROLLBACK` environment variable for the matches its own matchmaker
  starts when it runs. Both write `p2p` into the match config with the same rule (`Matches/P2P.cs`,
  `src/services/nodePort.ts` hasP2PHost: every match with a human who plays). The match flow serves the nodes
  (`RollbackCallbacks`, `NodeConfig`) and sends each game to its node (`MatchLaunches`: 127.0.0.1 and the port its client
  reported, `Rollback:P2PNodePort` when none). That port comes from `/api/identify` (the web service's since the
  identity port, docs/IDENTIFY.md; the TS one wrote the same record while it answered the route).
  `Rollback:P2P` takes `P2P_ROLLBACK` when it is not set itself, but a cluster setting changed through the control API is
  not seen by TS.
- **Until then:** do not run the TS matchmaker beside the C# one; change both together if it runs. Each executable that
  starts matches logs the C# value once it has started (the cluster settings are loaded by then).
- **Delete when:** the TS matchmaker is retired.

Retired with slice 3e (the realtime gateway in the TS websocket's place; numbers are not reused): 4 (rift and mission
progress reached the game through the TS websocket), 5 (`ovsctl player disconnect` through the TS websocket), 7 (the TS
websocket declined every Casual rematch), 9 (the match flow built configs from the TS websocket's channels, which still
sent them; with it the settings `MatchEnd:Enabled`, `GameplayConfigs:Mode` and `Realtime:Gateway`).

### 10. End Game's ranked-set XP is settled by the TS server and paid by C#

- **Decided:** 2026-10-04 (End Game), on the condition that it is recorded here.
- **What:** ratings and ranked sets are still the TS server's (6). When it settles a ranked set or a public FFA game,
  `awardRankedSetXp` / `awardFfaMatchXp` (`src/services/rankedSetXpService.ts`) decide who is paid and whether they
  won: a set needs one game played, so a pregame dodge pays nobody (not even the side given the win); after a
  game, whoever quit (a concede, a walkout, a dodge before game 2 or 3) gets nothing and everyone else is paid. They publish
  `{playerId, won, character, setKey, source}` for each player on `reward_tracks:ranked_set`. The C#
  `RankedSetXpSubscriber` (`Core/RewardTracks/RankedSetXp.cs`, in the match flow executable) pays it once per `setKey`
  and player (`ranked_set_xp:{setKey}:{playerId}`). The battle pass gets `RewardTracks:BattlePassSetXp`/`WinXp`, the
  account and the played character's levels get `CharacterSetXp`/`WinXp`, and the levels' completed tiers are paid
  at once. It tells the game on `ws:send` (RewardTrackStatesUpdated) and logs a `MIGRATION BRIDGE` warning.
- **Why:** the reward tracks are C#'s (`rewardtracks`), the sets are TS's.
- **Delete when:** ranked sets are settled in C# (with 6). The C# code that settles them calls the subscriber's
  payment directly, and the channel, the TS publish and this entry go.

## Not bridges (kept after the migration)

- The stream `match:results` (decided 2026-10-02): the http service that receives a match report appends it
  (`MatchResults`, at most ~10,000 kept); the match flow replicas read it as one consumer group (`MatchResultStream`),
  so each result is handled once, a result appended while no replica runs waits instead of being lost, and a replica
  that dies leaves its unacknowledged results to the others (XAUTOCLAIM after a minute; Redis 6.2+). It replaced the
  pub/sub channel `match:end_of_match_stats` the TS server published.

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
