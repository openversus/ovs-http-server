# Bans and player names

A ban is on a person, not an account: it holds every identifier known for them, and each one is refused on its own.
The identifiers are the IP, the Steam id, the Epic id, the hardware hash (only a strong version-2 one: a weak hash can
be shared by many machines), the install id, and the player id. The player id is what refuses a session token that is
still valid; the others refuse a login, including one that would make a new account.

## Where bans come from

All of these are merged; any one of them alone is enough to keep a ban in effect.

| Source | What | Written by |
|---|---|---|
| Ban files, one per kind (`Bans:IpFile`, `CidrFile`, `SteamIdFile`, `EpicIdFile`, `HardwareFile`, `InstallIdFile`) | One entry per line; a line starting with `#` is a comment; entries trimmed; hex ids in any case. Reread when changed. | Hand |
| `Bans:AutoBansFile` (`auto_bans.yaml`) | One record per ban the services made (below). Also read as a ban source. | The services, appended |
| Mongo `player_bans` | The same records. | The services |
| Mongo `bans` | `{kind: "ip" \| "cidr" \| "id", value}`: single values. | Hand |
| Redis sets `bans:ip`, `bans:cidr`, `bans:steam`, `bans:epic`, `bans:hardware`, `bans:install`, `bans:player`; `bans:id` (any identifier but the IP) | What every replica checks at once. Filled at the access service's start from Mongo and the auto-ban file, so either one alone restores every ban. Only added to. | The services |

Each identifier is checked against its own lists only. (The TS server checked the login's IP against every file, and
read its hash-ban file for the Steam and Epic lists, so no Steam or Epic ban was ever in effect.)

The services that ban (the access service) must be able to write the auto-ban file: mount its folder read-write for
them. A service that cannot write it logs an error at startup and keeps its bans in
Mongo and Redis only.

## A ban record

```yaml
- ban_id: '9f1c...'
  at: '2026-10-07T21:14:03.512Z'
  reason: 'banned name'
  source: 'namechange'        # namechange, login, sweep or manual
  player:
    id: '6a5d...'
    name_at_ban: 'the name they had'
    attempted_name: 'the name they tried'
    created_at: '2026-09-01T10:00:00Z'   # when the account was made (from its id)
  matched:
    list: 'banned_names'      # or the ban file / source an identifier matched in
    term: '...'
  identifiers:                # each one is banned
    ip: '...'
    steam_id: '...'
    epic_id: '...'
    hardware_id: '...'
    install_id: '...'
  request:
    ip: '...'
    user_agent: '...'
  online: true
  disconnected: true
```

## Where a ban is enforced

- **Login** (`POST /access`), in this order: the IP; then the identifiers the game sent, before any account is looked up
  or made (the lookup can write to other accounts); then the account's own identifiers and its player id; then its name
  (below). A banned login gets what the TS server sent: an empty 200.
- **Every request with a session token** (HydraToken) and **the realtime gateway's handshake and an edge's resume**:
  a banned player id is refused. Each service keeps `bans:player` in memory: a ban's `bans:changed` message adds the
  player at once, and the whole set is reread every 60 s.
- **Their connection** is closed when the ban is made (`ws:disconnect` through the player's replay log, so it reaches
  them across an edge's move to another node).
- **The sweep** (access service; one replica at a time): at start, when a ban file changes (once it has been still for
  2 s), when a ban is made, and every 60 s, it checks every online player through every identifier of their account. One
  who matches is cut off and their player id banned, recorded with source `sweep` and the identifier that matched; their
  other identifiers are not added (the matched one is banned already).

## Making and lifting a ban

`ovsctl player ban <who> --reason "..."` bans the person behind a player (source `manual`) and prints what was banned.
`<who>` is anything `player show` takes.

A ban is lifted by removing it everywhere it is: the ban files, the record in `auto_bans.yaml`, the record in Mongo
`player_bans` (and any `bans` entry), and each identifier from its Redis set, `bans:player` included. One left behind
keeps the person banned.

## Names

Three YAML lists, each a top-level `terms:` list of single-quoted strings (`Bans:BannedNamesFile`,
`ForceChangeNamesFile`, `AllowedNamesFile`). Every term is literal text: `$`, `+`, `@` and `!` are letters in them,
never pattern syntax. A blank term is skipped.

- **Banned** (`banned_names.yaml`): a name containing a term anywhere bans the person.
- **Force change** (`force_change_names.yaml`): a name containing a term as a whole word (neither neighbour a letter or
  digit; `_`, spaces and symbols separate words) must change.
- **Allowed** (`allowed_names.yaml`): words that contain a term but are fine. An occurrence that lies entirely inside
  an allowed word is not a hit; the same term elsewhere in the name still is.
- A banned term wins over a force-change term.
- Names are compared after folding lookalikes: fullwidth letters, accented Latin letters, and Cyrillic and Greek
  letters drawn like Latin ones; zero-width characters and combining marks are dropped; case is ignored. (The services
  run without ICU, so this is a table, not Unicode normalization: mathematical and circled letters are not folded.)
- A list file is reread when it changes, once it has been still for 2 s (an editor may still be writing it). A file that
  cannot be read keeps its last good terms and logs an error. Until the banned and force lists have loaded once, a name
  change is refused, never let through.

At login, a name that hits the banned list bans the person (source `login`); one that hits the force list is renamed to
the player's own generated `OpenVersus_` name, or a new one, before the session is written. The website's name change
(`/namechange`) is not ported yet.
