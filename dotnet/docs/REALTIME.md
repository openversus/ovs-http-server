# Realtime: the websocket, and how it leaves the TS server

The game keeps one websocket open to the server for the whole session. Everything the server tells the game without
being asked goes down it: a match was found, a player joined your party, your missions moved. Today that is the TS
websocket service (`src/websocket.ts`, its own container, one instance). This page is the plan for replacing it, and
the list of what still assumes there is only one of it.

## The goal

Websocket nodes are cattle, not pets: any number of them, any one restartable or replaceable at any time, and a player
never notices. That rules out keeping anything about a player in a node's memory that another node, or the next
version of the node, would need.

## What the TS websocket does today

- **The connection** (small): the game's first frame carries its session token (bytes 0x13-0x14 the token's length, the
  token, a 12-byte id, then a Hydra map); the server answers with a fixed id frame (`09 01 00 24` and a 36-character
  id), pings every 20 s (`0C`), the game answers (`0A`), and a game silent for 80 s is cut off. Everything else the
  server sends is a Hydra-encoded message (`HydraEncoder.Encode(..., webSocket: true)` in C#). The game sends nothing
  else.
- **About 25 Redis channels** (most of the 3,500 lines): the HTTP routes, the matchmaker and the rollback callbacks
  publish what happened, and the websocket turns it into messages for the players concerned. Several handlers also
  write Redis and Mongo (match end: ratings, toasts, rematch; disconnect: queue tickets, lobbies, sessions).

## The shape it moves to

- **Gateway nodes** hold sockets: the handshake, the ping, presence, and delivery. Delivery is one channel that every
  node hears (`ws:send`, `{playerIds, message}`): each node sends the message to the players it holds. A gateway runs
  no game logic, because a handler that writes would run once per node.
- **Game logic lives with whatever caused it**: the HTTP route, the matchmaker, the rollback callback endpoint, as Core
  services. They build the messages and send them through `ws:send`. Writes that could happen twice (two nodes, a retry)
  are made safe with a `SET NX` key, as ratings (`elo_processed:*`) and missions (`mission_match:*`) already are.
- **No player state in a node's memory.** What the TS websocket keeps per connection moves to Redis (decided
  2026-10-01):

  | Kept in memory today | Read by | In Redis |
  |---|---|---|
  | the queue ticket (`ticket`) | disconnect, a replaced connection | a pointer per player to their ticket |
  | the match's config (`matchConfig`) | perks lock, match end, disconnect | per player, beside the match notification, with the match's TTL |
  | a forced rejoin in progress (`pendingRejoin`) | the handshake, disconnect | `rejoin_pending:{player}`, short TTL |
  | the last answer to the ping (`lastPong`) | the 20 s check | already there: `player_heartbeats` (zset, ms) |
  | the 1 s "still searching" tick (`matchTick`) | cancel, disconnect | nothing: one sweep per node over its players with a queued ticket |

  Only "this socket is closed" stays local.

## Moving it without a big switch

A TS websocket handler only runs when something publishes its channel. When an HTTP route moves to C#, it stops
publishing the TS channel and sends its messages through `ws:send` instead, so the TS handler falls silent and nothing
is done twice. Each slice (a route and the messages it causes) can be tried in game while the TS websocket still holds
the sockets.

The exception is the part that shares the in-memory state above: queueing, match configs, perks, match end, disconnect
and rejoin. Those handlers move together with the gateway and the matchmaker. Until then, C# publishes the TS channels
they listen to as the TS server does (`matchmaking:cancel` from a party join, `match:notifications` and
`matchmaking:complete` from a rift start; MIGRATION-BRIDGES.md 2).

Done so far: rift progress, missions and reward tracks (MIGRATION-BRIDGES.md 4), and the party lobby routes (invite,
join, leave, mode, ready, loadout lock; the custom lobby side still on the TS server, MIGRATION-BRIDGES.md 6).

## A node restart without disconnecting anyone

State in Redis is necessary but not enough: the socket itself lives in one process, and the game logs out when it
closes. Two ways to close that gap:

1. **Drain**: a new node takes new connections, an old one stops taking them and keeps its sockets until they leave.
   Nothing to build; an old node can linger for hours.
2. **A thin edge** in front of the nodes: it holds the game's socket and forwards frames to a node; when that node goes,
   it connects to another and replays the game's first frame (the token and a fixed header), swallowing the second id
   frame (it is fixed). The game never sees it. The edge has no logic, so it rarely changes. Open question before
   relying on it: a session token can expire (`Access:TokenTtl`) while the player stays connected; a replay needs a
   token the server still accepts.

## Still assuming one instance (not to be ported as it is)

- The TS websocket empties the whole online set when it starts: with two nodes, that removes the other node's players.
  Presence has to be per node or kept alive by the heartbeat.
- Disconnect cleanup (queue ticket, lobby, session) runs only when the node that held the socket sees it close. A node
  that dies runs none of it: a reaper, under a lock, has to clean up after players whose heartbeat stopped
  (`ZRANGEBYSCORE player_heartbeats` older than 80 s).
- `player_heartbeats` is per player, not per connection: a stale socket on one node is kept alive by the player's new
  connection on another. It needs a connection id (in the member, or a current-connection key), which also lets a node
  close a connection that has been replaced elsewhere.
- Lobby updates read the whole lobby, change it and write it back. Two requests at once can lose one of the changes.
  Not new, and rare with two players in a party; worth a transaction when the lobby code is next reworked.

## Not ported

- `src/accelByteLobbyWs.ts`, the AccelByte text websocket on `/lobby`: added with the web party system (2026-02-23),
  before the in-game custom lobby (2026-03-21). The bench has never seen a connection to it. It goes with the TS HTTP
  server unless the production logs show the game using it.
- The website's custom lobby and party pages were retired on 2026-09-26; their server code (`services/customLobbyService.ts`,
  party keys) has no way in any more.
- `lobby:transition` (no publisher) and `party:member_join` (its publisher is never called): dead in the TS server.
