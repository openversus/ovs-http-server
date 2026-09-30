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
  joinedPlayerUsername, allPlayerIds, mode}`) and `client_update:modal` (`ClientUpdateGate`, show a player the update
  toast: `{playerId, nonce}`). Their payloads are JSON exactly as the TS server writes them.
- **Starting a match:** `IMatchLauncher` (`Core/Matches/`, for rifts so far) writes what the TS custom lobby writes
  when a match starts (`match:{id}`, `match:{id}:perks:{bot}`, the notification at `{id}`, `rollback:current_port` on
  demand) and publishes `match:notifications` (the TS `MATCH_FOUND_NOTIFICATION`) and `matchmaking:complete`. The TS
  websocket sends the match to the game and the TS rollback routes serve it. For modes the websocket does not know,
  the notification carries two fields added to the TS type for this (`src/config/redis.ts`): `gameplayConfigOverride`
  and `playerConfigOverrides`, merged over the websocket's PvP gameplay config in `handleSendGamePlayConfig`. Delete
  them with the websocket's port.
- **Lobby keys:** `lobby:{id}` (JSON written by the TS `ssc.ts` and `websocket.ts`; C# adds a player to it keeping every
  other field as read), `player_lobby:{player}` and `lobby_redirect:{id}` (read only, for now). C# also creates rift
  lobbies (`RiftLobbyService`, `create_rift_lobby`): the same `lobby:{id}` JSON, `player_lobby:{player}`,
  `player:{player}:lobby:{id}` and `connections:{player}` `lobby_id` as the TS `create_party_lobby` writes, with a mode
  the TS server never writes (`"rift_lobby"`) and three more fields (`riftConfigSlug`, `chapterGuid`,
  `chapterDifficulty`) that TS code reading `lobby:{id}` (joins, invites) will see. The rift loadout lock writes
  `player:{player}` `character`/`skin` as the TS `lock_lobby_loadout` does.
- **Asset sync:** `dataassets` and the `config` collection's `CRC` are written only by the TS server's `POST /syncAsset`
  (`dataAssetSync.ts`: the asset, then the CRC bumped). C# reads both (`HissService` builds its answer once per CRC;
  the inventory, cosmetics and hiss answers read the assets), so an asset sync goes through the TS server until
  `/syncAsset` is ported, and must keep bumping the CRC.
- **Delete when:** no TS service reads or writes that key, collection or channel any more. Then the C# side may change
  the shape, drop the mongoose quirks, and the contract comment goes.

### 3. `/batch` sends the sub-requests C# has not ported to TS (`src/OpenVersus.Server.Http/Batch/BatchRunner.cs`)

- **Decided:** 2026-09-29 (option 1, on the condition that it is recorded here and never becomes permanent).
- **What:** the C# `/batch` runs every sub-request through its own pipeline. Those that reach a stub (not ported), and
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

## Not bridges (kept after the migration)

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
