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
  | the match's config (`matchConfig`) | perks lock, match end, disconnect | per player, beside the match notification, with the match's TTL: `match_config:{player}` (the message as sent, 20 min; the perks lock rewrites it and keeps the TTL; `GameplayConfigs`) |
  | a forced rejoin in progress (`pendingRejoin`) | the handshake, disconnect | `rejoin_pending:{player}`, short TTL |
  | the last answer to the ping (`lastPong`) | the 20 s check | already there: `player_heartbeats` (zset, ms) |
  | the 1 s "still searching" tick (`matchTick`) | cancel, disconnect | nothing: one sweep per node over its players with a queued ticket |

  Only "this socket is closed" stays local.

## The gateway (`OpenVersus.Server.Realtime`, the `ws` service)

Built, not yet handed out by /access (the TS websocket still holds the players; the switch comes when every channel
below has moved). Each node:

- takes every websocket upgrade on its public port (WEBSOCKET_PORT), any path; the client's IP is the reverse proxy's
  forwarded one (`ClientAddress`, the rule every service uses). Any other request there but `/health/*` is answered
  `200 HTTP server is running`, as the TS server answered it;
- checks the first frame's session token as every service checks it (`AccessTokens.Verify`, Access:JwtSecret); a frame
  that holds none, or a bad or expired token, is closed with nothing sent (code 1000: the TS server's close had no code,
  which .NET cannot send). A socket that sends no first frame within Gateway:HandshakeTimeoutMs (30 s) is closed;
- answers with the id frame and a ping, pings every Gateway:PingIntervalMs (20 s; at most 25 s, the game gives up at
  about 30 s), and drops a game silent for Gateway:SilenceCutoffMs (61 s, checked at each ping);
- delivers `ws:send` to the players whose current connection it holds, encoding each message once
  (`HydraEncoder.Encode(..., webSocket: true)`, from the JSON read with a JS object's key order), and closes on
  `ws:disconnect {playerId, connectionId?, except?, code?, reason?}` (no code: dropped at once, the ops command's).

`GatewayPresence` (Core) keeps who is connected where:

- `realtime:conn:{player}`: the player's current connection (id, node, ip, at), its TTL renewed by the ping's answer.
  A second login claims it, and the connection it replaced is closed wherever it is (`ws:disconnect` with `except`;
  the TS websocket left it open). A close that is not the current connection's changes nothing.
- `online_players`, `player_heartbeats`, `active_ip_accounts:{ip}`: written at the handshake and each answer, and
  removed at once when the current connection closes (before anything else; the TS websocket removed `online_players`
  last), except while `rejoin_pending:{player}` lives. Nothing is cleared when a node starts.
- `realtime:connections`: a stream of connected, replaced and disconnected events (player, connection id, node, ip, the
  session token's SHA-256), for the services that act on them, each with its own consumer group. The lobbies service's
  (`LobbyDisconnects`, group `lobbies`) takes a disconnected player out of their party and custom lobby as the TS
  websocket's close did, unless they have connected or logged in again since; a close in a post-match window
  (`rejoin_pending:{player}`) is handled when the window ends. Not yet consumed: the queue ticket, the session keys, a
  pre-game dodge, the daily toast bonus popup.

Parity with the TS websocket: `tools/realtime/gateway_diff.mjs` (raw frames, closes, Redis writes) and
`tools/realtime/disconnect_diff.mjs` (what a dropped game's close does to the lobbies).

## Moving it without a big switch

A TS websocket handler only runs when something publishes its channel. When an HTTP route moves to C#, it stops
publishing the TS channel and sends its messages through `ws:send` instead, so the TS handler falls silent and nothing
is done twice. Each slice (a route and the messages it causes) can be tried in game while the TS websocket still holds
the sockets.

The exception is the part that shares the in-memory state above: queueing, match configs, perks, match end, disconnect
and rejoin. Those handlers move together with the gateway and the matchmaker. Until then, C# publishes the TS channels
they listen to as the TS server does (`matchmaking:cancel` from a party join and the game's cancel, `party:queued` from
the matchmaking request, `match:notifications` from a match's start; MIGRATION-BRIDGES.md 2). The channels whose TS
handler only built a message (a lobby join, matchmaking-complete, a toast, a ranked set's check-in, leaver and ranks)
are built by their C# publishers and sent through `ws:send` since slice 3b. Slice 3c moves the rest behind one cluster
switch, `Realtime:Gateway` (off: the TS websocket, as before): a launched match is appended to the stream `match:launched`,
and the match flow tells its players (`GameServerReadyNotification`, then the config: `MatchLaunches`) and ends it
(MatchEnd, whatever `MatchEnd:Enabled` says); the perks lock sends each game its config again once the perks are merged
(`PerksLock`); the rollback callbacks send `game-server-instance-ready`; a queued party's
ticket, its OnMatchmakerStarted and cancel are `MatchmakingQueue`'s, its 1 s tick the gateway's (`realtime:queued`,
`GatewayTicks`); the update toast is `ClientUpdateGate`'s, and the connection stays open (the TS websocket closed it
10 s later; an outdated player is turned away at each gameplay transition instead).

Done so far: rift progress, missions and reward tracks (MIGRATION-BRIDGES.md 4), the party lobby routes (invite,
join, leave, mode, ready, loadout lock) and the custom lobby (its routes, its messages, the match start; its match end
and rematch vote are ported too, `MatchEnd` and `Rematches`: the rematch routes answer from C#, the match end with
`MatchEnd:Enabled`), and the matchmaking worker (its own executable, `OpenVersus.Server.Matchmaking`; on by default, `Matchmaking:Enabled`; the queue side, the
tickets and their tick, stays with the websocket). The match config is built by the match flow (`GameplayConfigs`, kept per player; MIGRATION-BRIDGES.md 9), and still
sent by the TS websocket unless `Realtime:Gateway` is on.

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

Decided 2026-10-05: the edge, so that a node that dies (not only one being updated) drops nobody. A load balancer in
front cannot do it: after the upgrade it is a byte tunnel, and a node that goes closes the game's side. The chain is the
TLS-terminating router, then the edges (their own executable), then the gateway nodes. Facts it is built on: the game
keeps one websocket for its whole session, and goes back to its title screen when it has not been pinged for about 30 s
(so a re-attach must be done well within the 20 s ping's slack, or the edge pings the game itself meanwhile); a game
that reconnects logs in again first (/access). The gateway already keeps a connection's identity apart from the node
(its id, minted where the socket is held, in `realtime:conn:{player}` and every event), and takes the client's IP from
the forwarded headers; the edge will mint the id, pass the IP, and resume a connection on a new node with a check of the
session (not of the token's expiry). Whether messages sent during a re-attach are kept (a per-player sequence and a short
replay log) is still to decide.

## Still assuming one instance (not to be ported as it is)

- The TS websocket empties the whole online set when it starts: with two nodes, that removes the other node's players.
  Presence has to be per node or kept alive by the heartbeat. (The gateway clears nothing at start.)
- Disconnect cleanup (queue ticket, lobby, session) runs only when the node that held the socket sees it close. A node
  that dies runs none of it: a reaper, under a lock, has to clean up after players whose heartbeat stopped
  (`ZRANGEBYSCORE player_heartbeats` older than 80 s).
- `player_heartbeats` is per player, not per connection: a stale socket on one node is kept alive by the player's new
  connection on another. It needs a connection id (in the member, or a current-connection key), which also lets a node
  close a connection that has been replaced elsewhere. (The gateway's `realtime:conn:{player}`.)
- Lobby updates read the whole lobby, change it and write it back. Two requests at once can lose one of the changes.
  Not new, and rare with two players in a party; worth a transaction when the lobby code is next reworked.

## Not ported

- AccelByte (the text websocket on `/lobby` and the IAM endpoints): removed on 2026-10-02. It came with the web party
  system (2026-02-23), before the in-game custom lobby; production logs showed a single use ever, and the game itself
  never calls it.
- The website's custom lobby and party pages were retired on 2026-09-26; their server code (`services/customLobbyService.ts`,
  party keys) has no way in any more.
- `lobby:transition` (no publisher): dead in the TS server. (`party:member_joined` went with AccelByte.)
