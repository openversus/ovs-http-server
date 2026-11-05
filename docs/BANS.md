# Bans and player names

A ban is on a person, not an account: it holds every identifier known for them, and each one is refused on its own.
The identifiers are the IP, the Steam id, the Epic id, the hardware hash (only a strong version-2 one: a weak hash can
be shared by many machines), the install id, the device ids (docs/IDENTIFY.md: every one the account ever presented,
each kind's hash in one `device` kind), and the player id. The player id is what refuses a session token that is still
valid; the others refuse a login, including one that would make a new account.

A device id held by more accounts than `Bans:DeviceSharedLimit` (3) is left out of a person ban and listed in the record
as skipped, with the count: a machine id baked into an image, or a PC a household shares, would otherwise ban everyone
on it. 0 bans any. (A hardware hash has no such count: it is one per machine or absent.)

## Where bans are

Mongo is the source of truth; Redis holds what is in effect; the files are an import and a trail.

| Where | What |
|---|---|
| Mongo `player_bans` | One record per ban the services made: every identifier of the person, and the evidence (below). |
| Mongo `bans` | Single values `{kind, value}` (`ip`, `cidr`, `steam`, `epic`, `hardware`, `install`, and the older `id`: any identifier but the IP), imported from the ban files. |
| Redis sets `bans:ip`, `bans:cidr`, `bans:steam`, `bans:epic`, `bans:hardware`, `bans:install`, `bans:device`, `bans:player`, `bans:id` | The active values, which every check reads. Loaded from Mongo at the access service's start, after each import, and every minute (so a Redis emptied while running fills again); only added to: a lift removes its own. |
| Ban files, one per kind (`Bans:IpFile`, `CidrFile`, `SteamIdFile`, `EpicIdFile`, `HardwareFile`, `InstallIdFile`) | Edited by hand: one entry per line, `#` starts a comment line (the comment just above an entry is kept with it as its note). Imported into `bans` at the access service's start and when a file changes (once it has been still for 2 s): an entry only when Mongo has no record of that value, active or lifted. Never written. |
| `Bans:AutoBansFile` (`auto_bans.yaml`) | The trail: every ban and lift the services made, appended. Never read. |

A ban or a lift is never deleted: a lifted one gets `lifted_at`, `lifted_reason` and `lifted_source`, so the import never
brings it back and the history stays. Each identifier is checked against its own kind only. (The TS server checked the
login's IP against every file, and read its hash-ban file for the Steam and Epic lists, so no Steam or Epic ban was
ever in effect.)

The services that ban (the access service; the web service, for `/namechange`) must be able to write the trail: mount
its folder read-write for them. A service that cannot write it logs an error when it bans; its bans are still in Mongo
and Redis.

## A ban record

```yaml
- action: 'ban'
  ban_id: '9f1c...'
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
    device_ids:               # every device id of the person (current and past), minus the skipped ones
    - '...'
    device_ids_skipped:       # held by more accounts than Bans:DeviceSharedLimit: not banned
    - id: '...'
      holders: 7
  request:
    ip: '...'
    user_agent: '...'
  online: true
  disconnected: true
- action: 'lift'
  at: '2026-10-08T09:00:00.000Z'
  reason: 'why'
  source: 'manual'
  player: { id: '6a5d...', name: '...' }
  lifted_bans: ['9f1c...']
  no_longer_banned: ['Steam 7656...', 'Player 6a5d...']
  lifted_values: []
  still_banned: ['Ip 203.0.113.9 (bans.txt: ovsctl bans lift ip 203.0.113.9)']
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
  who matches is banned as a person, every identifier of theirs, recorded with source `sweep` and the identifier that
  matched, and cut off.

## Making and lifting a ban

- `ovsctl player ban <who> --reason "..."` bans the person behind a player (source `manual`) and prints what was banned.
- `ovsctl player unban <who> --reason "..."` lifts the player's ban records. Each of their identifiers that no other
  active ban holds is let through at once; one still held is listed with what holds it: another person's ban, or a ban
  file's entry (with the command that lifts it).
- `ovsctl bans lift <kind> <value> --reason "..."` lifts one ban file entry (`ip`, `cidr`, `steam`, `epic`, `hardware`,
  `install`, `device`, `id`); `ovsctl bans lift --all <who>` lifts every entry that is one of the player's identifiers, past or
  present (IP blocks excepted: they cover others; an IP of theirs inside one is listed as still banned). A lifted entry
  is kept, marked lifted, so the import does not bring it back and its line can stay in the file.

`<who>` is anything `player show` takes, or a player id. Every lift is recorded in the trail.

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
(`/namechange`, the web service) checks the new name as typed, before it is cut to 24 characters: a banned term bans the
person (source `namechange`, with the name they had and the one they tried, the request's IP and user agent) and the
page says so; a force-change term refuses the name; while the lists have not loaded, a name change is refused.

## Name history

Every name change is kept on the account (`nameHistory`, the last 200): the name before and after, when, the request's
IP, and the route (`web` for the website's `/namechange`, `manual` for `ovsctl player rename`, `login` for a forced
change at login). `ovsctl player show` lists the last ten. Nothing acts on it: it is there to be read when a name that
was fine at a glance turns out not to have been.

## Walking out of matches

The OpenVersus client reports every exit the game asks for itself (Alt+F4, the window's close button, the menu's quit) to
`POST /ovs/quit` just before the game goes; a kill from outside (Task Manager, a crash) is not seen. On the screens of a
matchmade match (the perk selection, the match, the rematch vote) there is no menu to quit by, so an exit there is a
walk-out. The access to it is the identify token, as `/ovs/notifications`.

The lobbies service counts it when the player was in a matchmade match (`player:{id}` status `in_match`, a Casual
rematch vote, or a ranked set) and was the first to leave that match: whoever is left can find their game stuck waiting,
and their only way out is the same exit, so a second leaver is never counted. A custom lobby and a match against bots
alone never count. `Quits:Limit` (3) counted quits within `Quits:WindowMinutes` (30) refuse the player's matchmaking
tickets (1v1, 2v2, FFA, Testing Grounds) for `Quits:LockoutMinutes` (15), the same way an outdated client's are, and
each refused request shows the banner `Quits:BannerTitle` / `Quits:BannerMessage` (`{minutes}` left). `Quits:Enabled`
off keeps the reports in the log and counts nothing. The account keeps two lifetime counters, `ragequit_total` (every
counted quit) and `ragequit_put_in_timeout` (every lockout); `ovsctl player show` lists the count in the window, the
lockout, and both.
