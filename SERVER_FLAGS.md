# Server flags

Feature switches read from `.env` (parsed in `src/env/env.ts`). A missing flag uses its
default, and every default below is the intended **production** behaviour, so nothing
needs to be set on live unless you want to change it. `.env` is gitignored and never
ships with a release.

Flags are read at startup: after changing one, restart the process(es) listed.
Jacob starts and restarts every server process.

| Flag | Default | Restart | What it does |
| --- | --- | --- | --- |
| `ENABLE_TEST_CHARACTERS` | `false` | HTTP | `false` keeps the retail client's test/unfinished characters out of everything the game is sent (catalog, HISS, unlocks, skins): `character_supershaggy`, `character_Meeseeks`, `character_C022`, `character_C033`, `character_cmanny`, `character_manny`, `character_C037`, `character_C099`. `true` serves them (local experiments only). List: `src/data/testCharacters.ts`. |
| `MISSIONS_ENABLED` | `false` | HTTP | `false`: players get no missions at all (daily, weekly, FTUE, battle-pass missions), so no mission progress ever populates. `true` restores the full mission set, which is kept in `get_or_create_mission_object`. |
| `CLIENT_VERSION_CHECK` | `true` | HTTP | Master switch for the client gate: the `MIN_CLIENT_VERSION` minimum and the `/api/identify` registration check. `false` lets any client version play. |
| `MIN_CLIENT_VERSION` | *(empty)* | HTTP | Oldest client version allowed into gameplay once set (e.g. `2026.09.23.1`). Empty = no minimum. Only enforced while `CLIENT_VERSION_CHECK` is on. |
| `CLIENT_RELEASE_REPO` | `openversus/ovs-client` | HTTP | The GitHub `owner/repo` whose latest release `/ovs/client-version` offers (the `.asi` and any paks). Only download URLs from that repo are offered. Leave it on live; set it locally (e.g. `tuggernuts1123/ovs-client`) to test updates from a fork's release. |

## Local-only settings (never on live)

These make local ranked matches reach the local dotnet rollback server
(`C:\DLLMVS\start_rollback.bat`, UDP 41234). Production uses on-demand rollback
instead, so do not copy them to a live `.env`.

| Setting | Local value | Why |
| --- | --- | --- |
| `USE_INTERNAL_ROLLBACK` | `1` | Clients are sent `UDP_SERVER_IP` (this PC) instead of `127.0.0.1`, so a second device (e.g. Steam Deck) reaches it. |
| `USE_INTERNAL_ROLLBACK_CPP` | `1` | Stops the HTTP server starting its built-in JS rollback server on the same port. |
| `ON_DEMAND_ROLLBACK_PORT_LOW` / `_HIGH` | `41234` / `41234` | The ranked worker's port counter always lands on 41234. |
| Redis `rollback:current_port` | `41234` | Seed once. A Redis flush resets it, and matches then get port 1 until it is set again. |
