# Arenas

Status: **not implemented, on hold** until the matchmaker is ported. Neither server handles any part of the mode. This
file records what is known so the work can start from evidence, not from scratch.

No WB-era Arena traffic has been captured anywhere. What follows comes from the game's own data and binary, one
capture made against the local bench, and the recollection of players who played the mode. Each item says which.

## How the mode is meant to play (player recollection)

- Parties queue together; a lobby holds roughly 10-20 players. Lobbies are **filled, never split**: a lobby with 4 open
  slots takes parties of 1-4 players, never a party of 5.
- At the start each player picks from a random subset of the roster, not the whole roster, with some starting gold
  (about 10); gold can be spent to reroll the character choices.
- Rounds are 2v2: a player and their partner against another pair from the same lobby. After each match there is a
  timed **buy phase**: buy perks and items (they accumulate until the end of the run), sell them back, or reroll the
  shop.
- Losing a round costs the **team** HP. A team at 0 HP is eliminated; the last team standing wins.
- **Bots are always enabled** in Arenas (unlike 1v1/2v2, where the bot fallback is switched off):
  - with an **uneven number of teams** in a round, one team plays a bot team that **mirrors it**: same characters,
    same perks and items;
  - if a lobby does **not fill** within a time limit, the empty slots are filled with bots. The limit is remembered as
    about 4 minutes, which matches the game's built-in default for `ArenaMatchmakingBotTimeout` (240 s).

## What turns the mode on (game data, confirmed on the bench)

The Arena entry is missing from the mode list because two values in the `hiss_amalgamation` answer keep it off:

| where | value served now | needed |
|---|---|---|
| `event-queue-config` → `evtq_arena.data.bAlwaysAvailable` | `false` | `true` |
| `feature-toggles` → `evtq_arena` | `false` | `true` |

With both set to `true` (and the CRC bumped so clients take the new answer), the Arena button appears and can be
selected. Both were changed at once, so which one is the real gate is untested. Queues whose `bAlwaysAvailable` is
`false` otherwise appear only while a calendar event lists them as an `MvsEventQueueHsda` component; the static
`get_hiss_calendar_events` answer has events for Testing Grounds, Action Sack and FFA, and none for `evtq_arena`.

Both values come from the TS sources the C# template is generated from (`src/data/eventQueue.ts` and the
`feature-toggles` block in `src/handlers/hiss_amalgation_get.ts`, via `tools/access/gen_templates.mjs`), so the change
belongs there, not only in `hiss-amalgamation.json`. **Do not turn the mode on for players before the matchmaker can
answer `arena-retail`**: until then the button leads to a queue that never ends.

Other Arena values already in the HISS answer: `evtq_arena` has `MatchmakingCriteriaSlug: "arena"`, `NumPlayers: 4`,
`bPartyEligible: true`, `GameModes: []`; `feature-toggles` has `ArenaShopLocalOperations: true` (suggests the shop
runs in the client); float setting `ArenaMatchmakingBotTimeout: 500` (the game's default is 240); `map-rotations` has
`maprotation_arena`.

## What the client sends (capture on the bench, 2026-09-30)

Selecting Arena and pressing PLAY, one player in a party lobby:

1. `PUT /ssc/invoke/set_mode_for_lobby`: `ModeString: "evtq_arena"`, `LobbyTemplate: "party_lobby"`, `LobbyId`,
   `HissCrc`, `Version`, preferences. The TS lobby service accepts it (`changeLobbyMode`).
2. `PUT /ssc/invoke/set_ready_for_lobby`: `Ready: true`; the answer has `bAllPlayersReady: true`.
3. 30 ms later, on its own: `POST /matches/matchmaking/arena-retail/request`. Same shape as the 1v1/2v2 requests:

   ```
   { data: { MultiplayParams: { MultiplayClusterSlug, MultiplayProfileId, MultiplayRegionId, MultiplayRegionSearchId },
             NoBots: true, crossplay_buckets: ["All", "PC"], version },
     game_server: { launch_data: { id: 1, profile } },
     match: <lobby id> }
   ```

   `NoBots: true` is added to every queue request by clients that set `PFG.PvPBots` to 0, as the OpenVersus client does
   to keep bots out of 1v1/2v2. **The Arena matchmaker must ignore it**: bots are part of the mode.
4. TS has no route for it and answers its catch-all `{ Crc, MatchmakingCrc }` with no ticket. The client then waits
   indefinitely on "submitting ticket", and the quit option is gone from the menu while it waits: the game has to be
   closed from outside. A queue answer without a ticket is therefore worse than an error for the player.

## The client's bot timer (binary, plus the capture above)

For queue `arena` the client always arms a timer of `ArenaMatchmakingBotTimeout − 10 + rand(0..20)` seconds (for
1v1/2v2 only when `PFG.PvPBots` ≠ 0). When it fires, the client cancels its ticket
(`POST /matches/matchmaking/request/{id}/cancel`) and invokes `PUT /ssc/invoke/bot_queue`; neither server implements
`bot_queue` for any mode. Addresses and the full chain: the matchmaking bot-fallback notes in the reverse-engineering
docs (`multiversus/docs/matchmaking-bot-fallback.md`).

Observed on the bench: with no ticket in the queue answer, the client sent nothing at all for 11.7 minutes, well past
the 490-510 s window. **The timer needs a ticket**, so bot fill cannot be exercised until `arena-retail` returns one.

Open: since each client cancels on its own timer, a per-client `bot_queue` cannot by itself keep a partly filled lobby
together. Filling empty slots with bots (and the mirror match) was most likely decided server-side; how the client
represents a bot participant is not yet read.

## Routes

| route | TS | C# |
|---|---|---|
| `POST /matches/matchmaking/arena-retail/request` | catch-all, no ticket | proxied to TS |
| `PUT /ssc/invoke/bot_queue` | not routed | not routed |
| `GET /arenas/{id}/instances` | not routed | stub |
| `GET /arenas/{id}/groups/{group}/participants` | not routed | stub |
| `GET /arenas/{id}/instances/{instance}/participants/{participant}` | not routed | stub |

## Next, when the work resumes

1. Port the matchmaker (tickets, the websocket `matchmaking-*` messages) and answer `arena-retail` with a ticket.
2. Read the client's Arena state types (`FMvsArenaData`, `FMvsArenaConstantInfo`, `FMvsArenaTeam`,
   `FMvsArenaPlayer`; `UMvsArenaSubsystem` reads them in `HandleGameplayConfigParsed`, so Arena state arrives in the
   gameplay config) and how a bot participant is represented.
3. Find what the `/arenas/*` routes must answer and when the client calls them.
4. Only then enable the two HISS values above, and decide which of them is the gate.
