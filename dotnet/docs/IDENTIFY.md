# The OpenVersus client's identity: /api/identify and the Steam ticket

The OpenVersus client registers who is at its IP before the game logs in (`POST /api/identify`, the web service) and
gets a token for its own calls (`GET /ovs/notifications`, and the game's `POST /access` when the game has no session
token of its own yet). The TS server took every identifier on the client's word and signed them into that token with
the game's `JWT_SECRET`, so anyone could register any Steam id and be that player. Two things change here.

## A Steam id needs the client's session ticket

The client sends `steamTicket` (hex) with its registration: a Steam session ticket from the game's own Steam API
(`ISteamUser::GetAuthSessionTicket`). Inside it is the app ownership ticket, which Steam signs (RSA-SHA1 with its system
key, embedded in `OpenVersus.Server.Identity/Steam/SteamTicketVerifier.cs`): the SteamID64, the app, the licenses, when it was made and when
it expires. `SteamTicket.Parse` reads the layout (SteamKit's steam3_appticket.hsl); the verifier checks the signature,
the expiry and the app (`Access:SteamAppId`, `STEAM_APP_ID`, 1818750; 0 accepts any app). A ticket with no signature
is refused, always.

- Verified: the ticket's SteamID64 is the client's Steam id, and the identity record and the token say so
  (`steamVerified: "1"`).
- Refused, or no ticket at all: the claimed `steamId` is logged and dropped. The client is identified by its install id,
  its hardware fingerprint and its IP, as any non-Steam client always was. An Epic id has a proof of its own (below).

The check is offline. What it proves: Steam signed this ownership ticket for this SteamID64 and this app, and it has
not expired. What it does not: that the ticket is fresh. Only the ownership ticket is signed and Steam reuses it for
about three weeks, so a copied ticket stands until it expires. Asking Steam itself closes that (below); the offline
check stays the floor: when Steam cannot be asked, it alone decides, and a login is never blocked by Steam being down.

## Asking Steam

The Steam identity service (`src/OpenVersus.Server.Identity.Steam`, the `steam` service: no public port, outbound to
Steam only, its own container) logs on to Steam as an anonymous game server for the app and does the game server side
of `BeginAuthSession` for each registered client's ticket: the ticket's session part goes to Steam in the server's auth
list, and Steam answers with a verdict on it (`ClientTicketAuthComplete`). Steam then holds that auth session for as
long as the game runs under that account: the moment the game closes, Steam says so (`AuthTicketCanceled`). A ticket
is single-use per session, and the service's list is the whole set of tickets it holds.

With `Steam:Enabled` (a cluster setting, on by default; off, the service idles disconnected and the offline check alone
decides; without the service running, on behaves as off):

- `/api/identify` queues the verified ticket for the service (`steam:auth:open`, a Redis list: a request outlives a
  service restart, and one queued while no service is connected is taken the moment one is) and, when a service is
  connected (`steam:status`), waits up to `Steam:IdentifyWaitMs` (2500) for the verdict. OK: the token also says
  `steamOnline: "1"`. Refused: the Steam id is dropped as a bad signature's would be, before the identity record is
  written. No verdict in time, or no service connected: the offline verdict stands, and a verdict that lands later is
  acted on by the service.
- The first verdict on a session decides. Refused (no license, VAC or publisher ban, invalid, someone else's session):
  the player is disconnected, as a ban does, and the login treats that Steam id as a claim for
  `Steam:RefusalHoldMinutes` (10). `AuthTicketInvalidAlreadyUsed` on a ticket this connection itself opened is a lost
  reply, not a refusal. No verdict within `Steam:VerdictTimeoutMs` (20000): unavailable, the offline verdict stands.
- Steam judges every entry again each time the list is re-sent (another player's open or end) and answers OK again
  for a held ticket: not a change. A verdict after OK that is not OK (the game closed, logged in elsewhere) ends the
  session's presence and nothing more. Family
  Sharing: the ticket's own Steam id is the player; the license owner (`owner_steam_id`) is recorded and never a
  ban's concern, in either direction.
- A drop of the connection to Steam loses every held session (tickets cannot be reopened): each becomes unavailable
  and, once Steam is back, its client is sent a `reidentify` notification (`GET /ovs/notifications`) to mint a new
  ticket and register again. A service restart does the same for what the earlier process held. A ban ends the held
  session (`steam:auth:end`).

What the service writes (`Core/Steam/SteamSessions.cs` has the keys): `steam:session:{steamId}` (24 h: `state` pending,
ok, refused, canceled or unavailable; `response`, Steam's word; `owner_steam_id`; `player_id`; `ticket_hash`; when),
`steam:online` (Steam id -> player id while ok), `steam:presence` (a message on every change) and `steam:status`
(10 s, refreshed while the service runs: without it nothing above is trusted). `ovsctl steam status` shows the
connection, the held sessions and the verdict counts; `ovsctl player show` adds the player's session.

Presence: a session that is ok means the game is running under that Steam account (the title screen included), which
is not "connected to our server" (the websocket's `online_players`). The profile lookups that paint an online indicator
(`/accounts/wb_network/bulk`, the player search) take Steam's word first where it has one: ok is online whether or not
the websocket is up yet; a game Steam saw close (or refused) within `Steam:PresenceOverrideMinutes` (5) is offline before
the reaper notices; everything else (no session, pending, unavailable, an older verdict, a session under a Steam id that
belongs to another player, no service running) leaves the websocket's answer. Match logic keeps reading
`online_players`: a game that lost its socket is still gone from the match.

## An Epic id needs the game's ID token

The client sends `epicToken` with its registration: the Epic account ID token the game's own Epic Online Services SDK
holds once the game is logged into an Epic account, a JWT Epic signs (RS256) for the game's client id, whose subject is
the Epic account id. The game never asks its SDK for one; the client does, on the game's platform handle, and
registers again with it when the login lands (an Epic launch logs in during startup; the first registration goes
without). Only an Epic Games Store launch logs into an Epic account: a Steam launch logs into EOS Connect alone
(measured 2026-10-08), so a Steam client never has one, and the Epic id it may read off the Epic launcher's files is
the launcher's account, not the game's.

The check (`src/OpenVersus.Server.Identity/Epic`, in-process like the offline ticket check): Epic's published keys
(`Epic:JwksUrl`, the JWKS of its OpenID discovery document; fetched at startup, again after `Epic:JwksRefreshMinutes`,
and at once, at most once a minute, when a token names a key not held), the signature, the issuer (one of
`Epic:Issuer`: the game's SDK issues `.../epic/oauth/v1` tokens, Epic's current discovery document names v2, both with
the same keys), the audience (`Epic:ClientId`, the game's EOS client id), expiry and not-before within
`Epic:ClockSkewSeconds`, and a 32-hex subject. The keys last fetched are the floor: when Epic cannot be reached they
serve on. There is no "ask Epic" for an ID token as there is for a Steam ticket: a token stands until it expires (two
hours from issue, as the game's SDK hands them out), and that is both the floor and the ceiling.

Enforced only with `Epic:Enabled` and a client id (`Epic:ClientId`); without one, Epic ids are taken as claimed, as
before, and a token is ignored. Enforced:

- Verified: the token's subject is the client's Epic id (a differing claimed `epicId` is logged; the token decides), and
  the identity record and the identify token say so (`epicVerified: "1"`). The login takes an Epic id from an identify
  token or from the record only with that proof.
- Refused (bad signature, another issuer or audience, expired, a key Epic does not publish): the claimed id is logged
  and dropped, and a proof the same install stored earlier is dropped with it. No token: the claim is logged and
  dropped. The client is identified by its install id, hardware and IP.
- Not judged (no keys from Epic yet, in a fetch outage before any succeeded): the claim stays in the record unverified,
  which the login ignores; nothing is refused, and the outage is logged once.

Nothing is ever removed from an account: an account keeps the Epic id it has (and its Steam id beside it; a player
has both when both were seen). A proved id, Steam or Epic, replaces one the account held as a claim when the login
reached the account by its install id, hardware or IP: that match says whose account it is, and the proved id is the
right one. An id the account holds proved (its ticket names it; an Epic id written with its proof, `epicVerifiedAt`)
is never displaced by a different proved one: two ticketed Steam accounts on one install are two people at a shared
PC, and the second gets an account of their own instead of the first's. What the proof otherwise changes is how a login finds the account: an Epic client older than the one that sends
the token is found by its install id, hardware and IP, which reach the same account, until it updates. The verifier was checked against a token the game's SDK handed out on an Epic launch (2026-10-08): the
claims are `iss`, `aud` (the client id), `sub` (the account id), `iat`/`exp` two hours apart, `t: id_token`, the
product, sandbox and deployment ids (`pfpid`, `pfsid`, `pfdid`), a display name, a nonce and a `jti`; a test verifies
such a token against Epic's live key when one is at hand. The `x-epic-id` header the account resolver still honors on
other routes is not covered by this check (the resolver hardening is a separate item).

## The token has its own secret

Identify tokens are signed with `Access:IdentifySecret` (`Access__IdentifySecret` in a container's environment; at least
32 characters, and it must differ from `Access:JwtSecret`). A game route's token check uses `Access:JwtSecret`, so an
identify token never passes as a game session, and a game session token never passes as an identify. The login
(`AccessService`) accepts either: the game's own token from its last login, or, when the game has none, the identify
token the client puts on the request, taking its Steam id only when `steamVerified` is set.

At the cutover from the TS server, rotate `JWT_SECRET` too: every identify token the TS server signed with it dies, and
a household behind one IP picks its web account again once.

## What is stored

- `identity:{ip}` (Redis, 5 minutes): the TS fields, plus `steamVerified` ("1" or "") and `steamTicket` (the verified
  ticket's decoded fields, extended JSON) so that a first launch, which has no account yet, can hand them to the login.
- The account (`playertesters`, field `steamTicket`): the verified ticket's decoded fields and its SHA-256
  (`ticket_hash`), when it was received and from which IP, replaced by each newer verified ticket. Never the ticket
  itself: it would replay. The field is absent on accounts that never sent a ticket, and the session-only fields
  (`token_generated_at`, `session_external_ip`, `connection_time`, `connection_count`) are null for a bare ownership
  ticket; every reader treats them so.
- Logins find an account by Steam id only through a verified one. Accounts that share a Steam id (made from claimed ids
  before tickets) are found in natural order, as the TS server found them, and logged.

## The client's other calls

- `GET /ovs/client-version?v=<running version>`: the update check. The latest GitHub release of `Clients:ReleaseRepo`
  (`CLIENT_RELEASE_REPO`, default openversus/ovs-client; anything that is not "owner/repo" means the default) is fetched
  with no credentials and cached for five minutes. The answer lists the files the in-game updater may install, both as
  `files` and flattened (`file_count`, `file_0_name`, ...): the one plugin (`OpenVersus.asi` or
  `OpenVersus_<version>.asi`, which must carry the release's version) and complete pak groups, only from that repo's
  release downloads and only with GitHub's sha256 digest; ZIPs and sidecars are never offered. `is_latest` compares the
  asking client with the release (a newer test build is "latest"); `update_required` is the gate's verdict
  (`Clients:VersionCheck`, `Clients:MinimumVersion`). A release that cannot be offered, or GitHub not answering, gives
  the TS fallback: no version, `is_latest` true, so a client is never sent backwards or into a broken release.
- `GET /ovs/all-players`: the first 200 accounts as `accountId` and `username`, for older clients' startup
  pre-registration; an empty list on any error.
