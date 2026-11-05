# Migration bridges

Everything here ties the C# services to the TS server **temporarily**, so routes can move over one at a time while
the game keeps working. None of it is meant to survive the migration. Each entry says what it does and exactly when it
gets deleted. When the condition is met, delete the code *and* the entry.

Anything that is a bridge logs a `MIGRATION BRIDGE` warning at startup naming this file, so a running bridge is never
invisible. If a new bridge is added, it gets an entry here and that warning, or it doesn't get merged.

## Active

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
  message to a game goes through `ws:send`, which the realtime gateway delivers.
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
- **Asset sync:** `dataassets` and the `config` collection's `CRC` are written by `POST /syncAsset` (the web service, as
  the TS `dataAssetSync.ts` wrote them: the asset, then the CRC bumped). `HissService` builds its answer once per CRC
  and the inventory, cosmetics and hiss answers read the assets, so a sync must keep bumping the CRC.
- **Delete when:** production runs on C# (the cutover). A fallback to the TS server after that is a restore of the
  backups taken just before the cutover (all of Mongo, Redis's append-only file), not a TS server reading what C# wrote;
  from then on the C# side may change the shapes, drop the mongoose quirks, and the contract comments go.

Retired when the TS server left the stack (every route ported; numbers are not reused): 1 (the YARP proxy in front of
the game, which sent the routes C# had not ported to the TS server; the game talks to the router), 3 (`/batch` sent the
sub-requests C# had not ported to the TS server as one TS batch; a sub-request another service owns still goes through
the router, `Batch:EdgeUrl`), 8 (P2P was switched in two places, `Rollback:P2P` and the TS matchmaker's `P2P_ROLLBACK`;
the TS matchmaker no longer runs, and `P2P_ROLLBACK` still fills `Rollback:P2P` through `TsEnvironment`), 10 (End Game's
ranked-set XP was settled by the TS server and paid by C#; C# settles the sets and FFA games and publishes the payment on
`reward_tracks:ranked_set` itself, `RankedSetXpPayout`, which `RankedSetXpSubscriber` in the match flow pays).

Retired with the port tail: 6 (ratings: the TS `PUT /matches/:id/leave` rated a leaver its own way; the route is C#'s
now and puts the leave on the match flow's `match:results` stream (`MatchLeaves`), where it is settled as the game's
websocket close is, once per match, under `RatedMatches`; no TS rating path is left).

Retired with slice 3e (the realtime gateway in the TS websocket's place; numbers are not reused): 4 (rift and mission
progress reached the game through the TS websocket), 5 (`ovsctl player disconnect` through the TS websocket), 7 (the TS
websocket declined every Casual rematch), 9 (the match flow built configs from the TS websocket's channels, which still
sent them; with it the settings `MatchEnd:Enabled`, `GameplayConfigs:Mode` and `Realtime:Gateway`).

## Not bridges (kept after the migration)

- The stream `match:results` (decided 2026-10-02): the http service that receives a match report appends it
  (`MatchResults`, at most ~10,000 kept); the match flow replicas read it as one consumer group (`MatchResultStream`),
  so each result is handled once, a result appended while no replica runs waits instead of being lost, and a replica
  that dies leaves its unacknowledged results to the others (XAUTOCLAIM after a minute; Redis 6.2+). It replaced the
  pub/sub channel `match:end_of_match_stats` the TS server published. Two more records ride on it: a leave said over HTTP
  (`leave`, `MatchLeaves`) and End Game's ranked-set and FFA XP (`set_xp`, `RankedSetXpPayout`, paid by
  `RankedSetXpPayer`; it was the pub/sub channel `reward_tracks:ranked_set`, which lost a payment made while the match
  flow restarted).

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
