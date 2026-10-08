# The OpenVersus client's identity: /api/identify and the Steam ticket

The OpenVersus client registers who is at its IP before the game logs in (`POST /api/identify`, the web service) and
gets a token for its own calls (`GET /ovs/notifications`, and the game's `POST /access` when the game has no session
token of its own yet). The TS server took every identifier on the client's word and signed them into that token with
the game's `JWT_SECRET`, so anyone could register any Steam id and be that player. Two things change here.

## A Steam id needs the client's session ticket

The client sends `steamTicket` (hex) with its registration: a Steam session ticket from the game's own Steam API
(`ISteamUser::GetAuthSessionTicket`). Inside it is the app ownership ticket, which Steam signs (RSA-SHA1 with its system
key, embedded in `Core/Steam/SteamTicketVerifier.cs`): the SteamID64, the app, the licenses, when it was made and when
it expires. `SteamTicket.Parse` reads the layout (SteamKit's steam3_appticket.hsl); the verifier checks the signature,
the expiry and the app (`Access:SteamAppId`, `STEAM_APP_ID`, 1818750; 0 accepts any app). A ticket with no signature
is refused, always.

- Verified: the ticket's SteamID64 is the client's Steam id, and the identity record and the token say so
  (`steamVerified: "1"`).
- Refused, or no ticket at all: the claimed `steamId` is logged and dropped. The client is identified by its install id,
  its hardware fingerprint and its IP, as a client without Steam always was (Goldberg and the Internet Archive build
  included). An Epic id is still taken as claimed: there is no ticket for it (yet).

The check is offline. What it proves: Steam signed this ownership ticket for this SteamID64 and this app, and it has
not expired. What it does not: that the ticket is fresh. Only the ownership ticket is signed and Steam reuses it for
about three weeks, so a copied ticket stands until it expires. Steam's online check (`BeginAuthSession` from a game
server) would close that; it is the next experiment.

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
