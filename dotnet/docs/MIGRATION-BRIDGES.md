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
- **Lobby keys:** `lobby:{id}` (JSON written by the TS `ssc.ts` and `websocket.ts`; C# adds a player to it keeping every
  other field as read), `player_lobby:{player}` and `lobby_redirect:{id}` (read only, for now).
- **Delete when:** no TS service reads or writes that key, collection or channel any more. Then the C# side may change
  the shape, drop the mongoose quirks, and the contract comment goes.

## Planned

### 3. `/batch` forwarding unported sub-requests to TS

- **Decided:** 2026-09-29 (option 1, on the condition that it is recorded here and never becomes permanent).
- **What:** once `/batch` is ported, the C# `/batch` answers the sub-requests it has ported itself and forwards the rest
  to the TS server. Each forwarded sub-request must carry the batch's `x-hydra-access-token`, `X-OVS-Identity` and client
  IP (`X-Real-IP`), as the TS `/batch` gives its sub-requests today (see `src/middleware/auth.ts` and
  `batchMiddleware.ts`). A sub-request that fails must never take the host down.
- **Why:** the login's two batches hold about 15 SSC/HISS calls; this lets each one move to C# on its own.
- **Delete when:** every route a batch can contain is ported (the SSC catch-all `SscUnlisted` included). Then the
  forwarding code, its settings and this entry go.

## Not bridges (kept after the migration)

- `TsEnvironment`: the TS server's environment variable names (`JWT_SECRET`, `WB_DOMAIN`, ...) fill C# settings, so the
  containers' `.env` files carry over. Configuration compatibility, not a runtime tie to TS.
