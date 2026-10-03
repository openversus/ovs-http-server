# Server flags

Feature switches read from `.env` (parsed in `src/env/env.ts`). A missing flag uses its
default, and every default below is the intended **production** behaviour. Live must
set `JWT_SECRET` (it has no default) and `MIN_CLIENT_VERSION` (the current client
release); everything else only when you want to change it. `.env` is gitignored and
never ships with a release.

Flags are read at startup: after changing one, restart the process(es) listed.

| Flag | Default | Restart | What it does |
| --- | --- | --- | --- |
| `ENABLE_TEST_CHARACTERS` | `false` | HTTP | `false` keeps the retail client's test/unfinished characters out of everything the game is sent (catalog, HISS, unlocks, skins): `character_supershaggy`, `character_Meeseeks`, `character_C022`, `character_C033`, `character_cmanny`, `character_manny`, `character_C037`, `character_C099`. `true` serves them (local experiments only). List: `src/data/testCharacters.ts`. |
| `MISSIONS_ENABLED` | `false` | HTTP | `false`: players get no missions at all (daily, weekly, FTUE, battle-pass missions), so no mission progress ever populates. `true` restores the full mission set, which is kept in `get_or_create_mission_object`. |
| `CLIENT_VERSION_CHECK` | `true` | HTTP | Master switch for the client gate: the `MIN_CLIENT_VERSION` minimum and the `/api/identify` registration check. `false` lets any client version play. |
| `MIN_CLIENT_VERSION` | *(empty)* | HTTP | Oldest client version allowed into gameplay once set (e.g. `2026.09.23.1`). Empty = no minimum, but a client must still have registered through `/api/identify`, which old C++ clients never do. Only enforced while `CLIENT_VERSION_CHECK` is on. |
| `STEAM_APP_ID` | `1818750` | HTTP | The Steam app a login's Steam ticket must be for (MultiVersus). A ticket for another app is ignored. `0` accepts any app. |
| `JWT_SECRET` | *(none, required)* | all | Signs every token: game sessions, the websocket handshake, `/api/identify` and the account-picker cookie. At least 32 characters; the server refuses to start without it or with the old hardcoded value. Use one long random value on index, mm and ws alike, and never commit it. Changing it logs everyone out once. |
| `ACCESS_TOKEN_TTL` | `24h` | HTTP | How long a game session token from `/access` stays valid: seconds (`86400`) or a number with `s`, `m`, `h` or `d` (`24h`, `7d`). Set it empty to make tokens valid until `JWT_SECRET` changes. The game gets a new token at every launch and reconnect; once one expires, the next request that needs the server is refused, and the game goes back to its title screen ("Disconnected") and logs in again with one click. An invalid value stops the server at startup. |
| `MATCHUPDATEKEY` | `MisconfiguredMatchUpdateKey` | HTTP | The key the rollback server sends as the `MatchUpdateKey` header on `/ovs_match_status`. Required: a request without it is rejected, as is a wrong one. Must match the rollback server's `Server__MatchUpdateKey`. |
| `P2P_ROLLBACK` | `0` | HTTP, mm, ws | `1` runs eligible matches (exactly two humans, no spectators; ranked, casual and custom alike) on the players' own machines: each game is told to connect to `127.0.0.1:P2P_NODE_PORT`, where the OpenVersus node runs; the match config's host runs the rollback engine inside its node and the other node forwards to it over a hole-punched path (the rendezvous service pairs them). Players without a running node cannot play P2P matches, so leave this `0` until the client ships the node. |
| `P2P_NODE_PORT` | `41234` | HTTP, ws | The UDP port a player's game is sent to for a P2P match when the player's client reported no node port. A current client starts its node on any free port and reports it at `/api/identify` (`nodePort`), kept in `connections:<id>`; this default is for clients that do not. |
| *(relay)* | | HTTP | A P2P match gets no rollback server at creation. When a node reports that no direct path opened (`/ovs_p2p_failed`, match key), the first report deploys the on-demand rollback server on the port the match was given, every report is answered with its address, and the nodes forward their games there. |
| `CLIENT_RELEASE_REPO` | `openversus/ovs-client` | HTTP | The GitHub `owner/repo` whose latest release `/ovs/client-version` offers (the `.asi` and any paks). Only download URLs from that repo are offered. Leave it on live; set it locally (e.g. `your-name/ovs-client`) to test updates from a fork's release. |

## Local-only settings (never on live)

These make local ranked matches reach a rollback server running on the same PC
(UDP 41234). Production uses on-demand rollback instead, so do not copy them to a
live `.env`.

| Setting | Local value | Why |
| --- | --- | --- |
| `USE_INTERNAL_ROLLBACK` | `1` | Clients are sent `UDP_SERVER_IP` (this PC) instead of `127.0.0.1`, so a second device (e.g. Steam Deck) reaches it. |
| `USE_INTERNAL_ROLLBACK_CPP` | `1` | Stops the HTTP server starting its built-in JS rollback server on the same port. |
| `ON_DEMAND_ROLLBACK_PORT_LOW` / `_HIGH` | `41234` / `41234` | The ranked worker's port counter always lands on 41234. |
| Redis `rollback:current_port` | `41234` | Seed once. A Redis flush resets it, and matches then get port 1 until it is set again. |
