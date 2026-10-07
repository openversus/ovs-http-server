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

The game's realtime connection (/access hands out its address: `Realtime:Domain` and `Realtime:Port`). Each node:

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

- `realtime:conn:{player}`: the player's current connection (id, node, ip, at, the session token's SHA-256), its TTL
  renewed by the ping's answer.
  A second login claims it, and the connection it replaced is closed wherever it is (`ws:disconnect` with `except`;
  the TS websocket left it open). A close that is not the current connection's changes nothing.
- `online_players`, `player_heartbeats`, `active_ip_accounts:{ip}`: written at the handshake and each answer, and
  removed at once when the current connection closes (before anything else; the TS websocket removed `online_players`
  last), except while `rejoin_pending:{player}` lives. Nothing is cleared when a node starts.
- `realtime:connections`: a stream of connected, replaced and disconnected events (player, connection id, node, ip, the
  session token's SHA-256), for the services that act on them, each with its own consumer group. The lobbies service's
  (`LobbyDisconnects`, group `lobbies`) takes a disconnected player out of their party and custom lobby as the TS
  websocket's close did, unless they have connected or logged in again since; a close in a post-match window
  (`rejoin_pending:{player}`) is handled when the window ends. A disconnected or replaced connection's queue ticket goes
  at once (`MatchmakingQueue.DropAsync`), and the rest of its party is cancelled and told. The session (`connections:{player}`,
  its cosmetics copy, `player:{player}*` and the IP's copy) goes last, only while it is still the closed connection's.
  The match flow's (`MatchDisconnects`, group `matchflow`) does what the TS close did for the match: a pregame dodge, a
  mid-game leave of a set game, a leave between a set's games (`MatchStatusEvents.GameClosedAsync`); for a P2P match it is
  the only such signal (a node sends no PlayerDisconnect). The access service's (`DailyToastPopups`, group `access`)
  shows a game that connects the daily toast bonus /access granted it (OnRewardsGranted, as the TS handshake did).
- A node that dies closes nothing. The other nodes take its players offline as their close would have
  (`GatewayReaper`, every `Gateway:ReapIntervalMs`): a player who has not answered a ping for `Gateway:ReapAfterMs`
  (30 s) and whose connection is on a node that is gone (stopped, or missing from the instance registry for 10 s, so
  about 30 s after a crash) gets a disconnected event marked reaped, which the readers handle as any close but for the
  match: there it is the server's failure (a crash, unrated; a set between its games is dropped at its next check-in).
  One script per player acts only while the connection is still the current one on that node, so several nodes reaping
  at once reap a player once, and a connection the edge moves to another node is left alone. A player whose game logs in
  again first never gets one (their new connection replaces the dead one); if a custom lobby is left over from that
  session, their first `create_party_lobby` takes them out of it rather than into it (/access records it in the session).

Parity with the TS websocket: `tools/realtime/gateway_diff.mjs` (raw frames, closes, Redis writes) and
`tools/realtime/disconnect_diff.mjs` (what a dropped game's close does to the lobbies; its reap mode kills the node
holding the game instead, and compares that with the close).

## Moving it without a big switch

How it was moved (history; the TS websocket is not run any more). A TS websocket handler only runs when something
publishes its channel. When an HTTP route moved to C#, it stopped publishing the TS channel and sent its messages through
`ws:send` instead, so the TS handler fell silent and nothing was done twice, and each slice could be tried in game while
the TS websocket still held the sockets.

The exception is the part that shares the in-memory state above: queueing, match configs, perks, match end, disconnect
and rejoin. Those handlers moved together with the gateway and the matchmaker. The channels whose TS handler only built
a message (a lobby join, matchmaking-complete, a toast, a ranked set's check-in, leaver and ranks) were built by their C#
publishers and sent through `ws:send` from slice 3b. Slice 3c moved the rest behind one cluster switch, which slice 3e
removed with the TS websocket's side of it: a launched match is appended to the stream `match:launched`, and the match
flow tells its players (`GameServerReadyNotification`, then the config: `MatchLaunches`) and ends it (`MatchEnd`); the
perks lock sends each game its config again once the perks are merged
(`PerksLock`); the rollback callbacks send `game-server-instance-ready`; a queued party's
ticket, its OnMatchmakerStarted and cancel are `MatchmakingQueue`'s, its 1 s tick the gateway's (`realtime:queued`,
`GatewayTicks`); the update toast is `ClientUpdateGate`'s, and the connection stays open (the TS websocket closed it
10 s later; an outdated player is turned away at each gameplay transition instead).

Everything the TS websocket did is C#'s: rift progress, missions and reward tracks, the party lobby routes, the custom
lobby with its match end and rematch (`MatchEnd`, `Rematches`), the matchmaking worker (its own executable,
`OpenVersus.Server.Matchmaking`), the queue and its tick, the match config (`GameplayConfigs`, kept per player, sent by
the match flow), and the disconnects (above).

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
session (not of the token's expiry).

Decided 2026-10-07: nothing sent during a re-attach is lost. Every message and forced close for a player goes through
one helper (`PlayerMessages.SendAsync` and `DisconnectAsync`), which runs one script: append it to the player's replay
log (`realtime:out:{player}`, a stream; field `message` or `disconnect`), then publish it with the entry's stream id
(`ws:send` gets `seqs: {player: id}`, `ws:disconnect` gets `seq`). The id is the player's sequence. The log keeps the
last `PlayerMessages.ReplayWindow` (60 s, by the Redis clock) and expires `ReplayTtl` (5 min) after its newest entry.
The window must be longer than any re-attach can take (the edge gives up sooner, and the reaper lets a dead node's
player go after about 30-50 s); then everything a trim drops was already delivered, and a re-attach replays whatever
follows the last id the game received. The nodes ignore the sequences until they serve an edge. A node's own frames (the
id frame, the ping, the matchmaking tick) are not logged: whichever node holds the game makes them.

## Still assuming one instance (not to be ported as it is)

- The TS websocket empties the whole online set when it starts: with two nodes, that removes the other node's players.
  Presence has to be per node or kept alive by the heartbeat. (The gateway clears nothing at start.)
- Disconnect cleanup (queue ticket, lobby, session) runs only when the node that held the socket sees it close. A node
  that dies runs none of it. (The gateway's reaper, above.)
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
