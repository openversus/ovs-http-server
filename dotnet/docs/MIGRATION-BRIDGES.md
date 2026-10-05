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
  `lobby:player_joined` (`PartyLobbyService`, a player joined someone's lobby: `{lobbyId, ownerId, joinedPlayerId,
  joinedPlayerUsername, allPlayerIds, mode}`), `client_update:modal` (`ClientUpdateGate`, show a player the update
  toast: `{playerId, nonce}`), `matchmaking:cancel` (`PartyService`, someone joined a party: `{playersIds,
  matchmakingId: "party-changed"}`; `MatchmakingRequestService`, the game's cancel: `{playersIds, matchmakingId}`; the TS
  websocket keeps the queue ticket and its tick in memory, so it cancels them) and `party:queued`
  (`MatchmakingRequestService`, the matchmaking request's ticket, as queueMatch builds it; the TS websocket sends the game
  OnMatchmakerStarted and pushes the ticket onto the list its `matchType` names, `1v1`, `2v2`, `FFA` or Casual's, which
  the matchmaker reads),
  `perks:notifications` (`PerksLock`, every player of a match has locked their perks: `{containerMatchId, playerIds}`;
  the TS websocket puts the perks into each player's match config, which it holds in memory, and sends it again) and
  `toast:received` (`MatchToasts`, a toast after a match: `{toasterAccountId, toasterUsername, toasteeAccountId,
  containerMatchId}`; the TS websocket grants the toastee 2 match_toasts and shows them the toast).
  Their payloads are JSON exactly as the TS server writes them. The party routes' other messages to players
  (`OnLobbyModeUpdated`, `InviteReceivedForLobby`, `PlayerJoinedLobby`, `PlayerLeftLobby`, `PlayerReadyForLobby`,
  `OnPlayerLoadoutLocked`) are built in C# and go through `ws:send` (4), as the TS websocket would have built them; so
  are the custom lobby's (`CustomLobbyService`), which the TS server published on `custom_lobby:notification` for its
  websocket to relay. That channel is now published only by the TS websocket itself (a player who disconnects leaves
  their lobby) and goes with it.
- **Starting a match:** `IMatchLauncher` (`Core/Matches/`, for rifts and custom lobbies) writes what the TS custom lobby
  writes when a match starts (`match:{id}`, `match:{id}:perks:{bot}`, the notification at `{id}`, `rollback:current_port`
  on demand) and publishes `match:notifications` (the TS `MATCH_FOUND_NOTIFICATION`, with a custom game's settings and
  its spectators) and `matchmaking:complete`. The TS
  websocket sends the match to the game and the TS rollback routes serve it. For modes the websocket does not know,
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
  server's. `rematch_accept`, `rematch_decline` and the match end stay on the TS server until the match flow moves.
- **Matchmaking** (`MatchmakingWorker` in its own executable, `OpenVersus.Server.Matchmaking`; on unless `Matchmaking:Enabled` is false): the queues `1v1`, `2v2` and `FFA` (ticket JSON lists the TS
  websocket fills when a party queues and empties on a cancel or disconnect), `player_heartbeats`,
  `player:{id}:blocked` and `player:{id}` `ip` are read as the TS worker reads them; a match writes what the TS worker
  writes (`match:{id}` with the tickets as queued, the notification at `{id}`, `ranked_set:{id}`,
  `player_ranked_set:{player}`) and publishes `match:notifications` and one `matchmaking:complete` per ticket. Each
  queue is worked under the TS lock (`matchmaking:lock:{queue}`), so the C# and TS workers can run side by side. The
  maps it picks from are a copy of the TS `src/data/maps1v1.json` / `maps2v2.json` (`Matchmaking/maps.json`,
  `tools/matchmaking/gen_maps.mjs`): a map change goes to the TS files and is generated again (`--check` tells) until
  the TS worker and websocket (which reads those files for hazards) are gone. An `FFA` match is mode `FFA` in the
  notification, from which the TS websocket sends it unranked as `evtq_ffa`; it has no ranked set. Outside the FFA
  window (`Ffa:WeekendOnly`, `Matchmaking/FfaSchedule.cs`) the matchmaker empties the `FFA` list and publishes
  `matchmaking:cancel` for each ticket, as the TS worker did. The TS website reads its own `FFA_WEEKEND_ONLY` (the FFA
  searching count, shown only while open), which `Ffa:WeekendOnly` takes when not set itself: keep the two the same.
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

### 4. Rift and mission progress from TS match results, pushed through the TS websocket

- **Decided:** 2026-09-30, on the condition that it is recorded here: each piece has to be ported to C#.
- **What:** the TS `submit_end_of_match_stats` (`src/handlers/ssc.ts`) publishes every result as it arrives on
  `match:end_of_match_stats` (`{matchId, playerId, winningTeamIndex, missionUpdates}`, the last being the
  submitter's own counters from `EndOfMatchStats.PlayerMissionUpdates`, which the rift stars are judged from), before its own processing, which is unchanged.
  Both C# subscribers run in the match flow executable (`OpenVersus.Server.MatchFlow`), not the HTTP service.
  The C# `RiftResultSubscriber` (`Core/Rifts/RiftProgressService.cs`) records the progress of rift matches (known by
  `rift_match:{match}`) and tells the game by publishing on `ws:send` (`{playerIds, message}`), a generic channel the
  TS websocket (`src/websocket.ts`) answers by sending `message`, as it is, to each connected player named. The C#
  service logs a `MIGRATION BRIDGE` warning at startup for this. Missions (with `Missions:Enabled`) hear the same
  channel: `MissionResultSubscriber` (`Core/Missions/MissionProgress.cs`) moves the player's missions, reading the
  match as the game saw it from the TS match notification at `{matchId}` and the character from `rift_match:{match}`
  or `player:{id}`, and pushes `MissionUpdatesComplete` (a `profile-notification`) on `ws:send`; it logs its own
  `MIGRATION BRIDGE` warning.
- **Why:** the client never asks for its rift progress; the server works it out from the match result and pushes it
  (`OnLobbyRuntimeDataUpdated`, `OnLobbyRiftStateUpdated`). The result reaches only the TS server, and only the TS
  websocket reaches the game. Taking over `submit_end_of_match_stats` instead would have put every ranked and casual
  match's stats through new forwarding code for the sake of rifts.
- **Delete when:** `submit_end_of_match_stats` is ported to C# and the websocket is ported (`ws:send` is then answered
  by the C# gateway). The hand-off itself stays, as a Redis Stream with a consumer group instead of this channel
  (decided 2026-10-02): the HTTP service that receives a result appends it, one match flow replica records and
  acknowledges it, and a result appended while no replica runs (a restart, a deploy) waits instead of being lost, as a
  pub/sub message is. It cannot change before then: the TS publisher only knows the channel.

### 5. `ovsctl player disconnect` closes the connection through the TS websocket

- **What:** the control API (`POST /control/ops/players/{who}/disconnect`, `OpsService.DisconnectPlayerAsync`)
  publishes `ws:disconnect` (`{playerId}`); the TS websocket (`src/websocket.ts`) closes that player's socket with
  `terminate()`, the path its heartbeat timeout takes, so the usual cleanup runs and the game logs out. The command
  refuses when no websocket service is subscribed (the publish reached nobody). Every C# service logs a
  `MIGRATION BRIDGE` warning for it at startup (each has the control API).
- **Why:** a client stuck on an unanswered call has no way out of the menu; this frees it without restarting the game
  or the websocket service.
- **Delete when:** the websocket is ported: C# closes the socket itself; the channel and its TS handler go.

### 6. Ratings (ELO) are still the TS server's, and only one of its three paths knows C#'s matches

- **What:** ratings change in three TS places: the match result (`processMatchResult`, services/eloService.ts, from
  `/ovs_end_match`), a pre-game dodge (`handlers/match_status.ts`) and a disconnect after the start (`websocket.ts`).
  The match result skips a match whose `match:{id}` has `isPasswordMatch`; the other two skip only a match config with
  `isCustomGame`, and neither leaves bots out. Every match C# starts (`MatchLauncher`: rift nodes, custom lobbies; the
  Casual queue's matches too) has `isPasswordMatch` and a mode of 1v1 or 2v2; a custom lobby's and the Casual queue's
  (human or bots: `BotDefaults.UnrankedNotificationFields`) also have `isCustomGame`, which keeps them out of all three
  and out of the TS best-of-3 sets. A rift match has not: a player who leaves one before or during it is charged on their
  regular 1v1/2v2 rating, and the bot gets a rating document. Read in the code, not seen yet: the bench's eloratings had no bot ids
  (2026-10-02). TS is not changed for it (it is going away).
- **Rule (for the port):** ratings count for the regular 1v1/2v2 queues only (ranked sets included); never rifts, custom
  lobbies or Casual (Casual may get a rating of its own, kept apart). The C# port of these three paths decides "does this
  match count" in one place, used by all three, and never rates a bot.
- **Until then:** do not ship rift matches to prod before these paths are ported, or accept the leak there (giving rift
  matches `isCustomGame` would close it, untested: it also changes what the TS websocket does at a rift match's end).
- **Delete when:** match results, dodges and disconnects are ported (match flow) with that one check.

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
- **Delete when:** match end moves to C# (the match flow lifecycle and the realtime gateway), with the rematch above.

### 8. P2P is switched in two places, and its match flow is still the TS server's

- **What:** whether eligible matches run P2P (on the players' own nodes) is `Rollback:P2P` for the matches C# starts
  (`MatchLauncher`: custom lobbies, the Casual queue, rift nodes; the C# matchmaker) and the TS server's `P2P_ROLLBACK`
  environment variable for the ones it still starts: a ranked set's next game (`createNextSetMatch`), a custom lobby's
  rematch, and its own matchmaker when it runs. Both write `p2p` into the match config with the same rule
  (`Matches/P2P.cs`, `src/services/nodePort.ts` hasP2PHost: every match with a human who plays); the rest of a P2P match is TS: `/api/identify` (the node's port), the websocket
  (sends the game to `127.0.0.1` and that port), `/ovs_register` (holds game-server-instance-ready),
  `/ovs_p2p_ready` and `/ovs_p2p_failed` (C# stubs, forwarded by the proxy). `Rollback:P2P` takes `P2P_ROLLBACK` when it
  is not set itself, but a cluster setting changed through the control API is not seen by TS: with the two different, a
  set's first game and its next ones can disagree.
- **Until then:** change both together; each executable that starts matches logs the C# value at startup.
- **Delete when:** set continuations, custom lobby rematches, `/api/identify`, `/ovs_register` and the two P2P routes
  are ported (match flow), the TS matchmaker is retired, and the websocket reads `p2p` and the node port from C#'s
  config (the realtime gateway).

### 9. End Game's ranked-set XP is settled by the TS server and paid by C#

- **Decided:** 2026-10-04 (End Game), on the condition that it is recorded here.
- **What:** ratings and ranked sets are still the TS server's (6). When it settles a ranked set or a public FFA game,
  `awardRankedSetXp` / `awardFfaMatchXp` (`src/services/rankedSetXpService.ts`) decide who is paid and whether they
  won: a pregame dodge pays nobody, and a concede pays the side that stayed. They publish
  `{playerId, won, character, setKey, source}` for each player on `reward_tracks:ranked_set`. The C#
  `RankedSetXpSubscriber` (`Core/RewardTracks/RankedSetXp.cs`, in the match flow executable) pays it once per `setKey`
  and player (`ranked_set_xp:{setKey}:{playerId}`). The battle pass gets `RewardTracks:BattlePassSetXp`/`WinXp`, the
  account and the played character's levels get `CharacterSetXp`/`WinXp`, and the levels' completed tiers are paid
  at once. It tells the game on `ws:send` (RewardTrackStatesUpdated) and logs a `MIGRATION BRIDGE` warning.
- **Why:** the reward tracks are C#'s (`rewardtracks`), the sets are TS's.
- **Delete when:** ranked sets are settled in C# (with 6). The C# code that settles them calls the subscriber's
  payment directly, and the channel, the TS publish and this entry go.

## Not bridges (kept after the migration)

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
