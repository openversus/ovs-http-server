# Seasons

What the server tells the game about seasons, what the game does with it, and what is still tied to Season 5. Read
this before changing `Season:Current`, porting the websocket's ranked updates, or starting a new ranked season.

## The current season

The game asks at login which season is current: `POST /ssc/invoke/attempt_daily_refresh` answers `CurrentSeason`, a
gameplay tag (`Season:SeasonFive`, `Season:SeasonSix`, ...). The C# server takes it from the setting `Season:Current`
(default `Season:SeasonSix`, `Core/Seasons/Seasons.cs`); the TS server always said `Season:SeasonFive`. The game lists
rift seasons up to the current one (the Season 6 rogue rifts need Season 6), and treats every earlier season as
finished.

## The end-of-season screen (ranked reset notice)

For each finished season, the game checks the player's ranked data for that season (`SeasonalData.<season>.Ranked`).
When `bEndOfSeasonRewardsGranted` is not true, it shows its end-of-season screen at login, and when the screen's
animation ends it sends:

    PUT /ssc/invoke/ranked_claim_end_of_season_rewards   {"Season": "Season:SeasonFive"}

It reads `RewardsGranted` from the answer to show rewards; with none, it goes on to its season-reset notice. Client
code (build f97148ff): the screen 0x142860fc0, the claim request 0x142a12260, the answer's handler 0x142860ae0.

Where the flag comes from:

- The login's profile (`/access`, `login-response.json`): Seasons 2 to 4 say `true`; Season 5 has no such field.
- `GET /ssc/invoke/ranked_data` (`Core/Leaderboards/RankedDataService.cs`): Season 5's flag is true once the player has
  claimed it. Claims are stored in the `endofseasonrewards` collection, `{_id: ObjectId(player), seasons: [...]}`,
  which only the C# server reads. The TS server never recorded the claim (its catch-all answered), so while Season 5
  was not current it showed the screen at every login. Now each player sees it once per season.
- The TS websocket's `FullRankUpdate` push after a ranked match (see the first open item).

No rewards are granted: the claim answers what the TS catch-all answered (`{Crc, MatchmakingCrc: 1}`,
`return_code` 200).

## Open items

1. **The TS websocket still sends Season 5 as not granted.** `src/websocket.ts` builds `FullRankUpdate` after a ranked
   match (two places: the regular result and the concede path) with `SeasonalData["Season:SeasonFive"]` and
   `bEndOfSeasonRewardsGranted: false`, whatever the player has claimed. If the end-of-season screen shows up right
   after a ranked match, this is why. **When:** the websocket's ranked updates are ported to C#: build them with
   `RankedDataService` (or the same claim lookup), and key them by the season the ranked data is for.
2. **Season 6 data is backfilled, not kept.** Decided 2026-09-30: fill what Season 6 is missing from Season 5, or from
   real Season 6 data where it exists. Done:
   - `ranked_data`: while a later season is current, it gets an entry too, with the same ratings (they are not kept
     per season) in the shape WB answered a running season with: no `FinalLeaderboardRank` in a mode, no
     `bEndOfSeasonRewardsGranted` (a live export of May 2025, when Season 6 was current, answered Seasons 2 to 6).
   - The login profile: Season 6's `SeasonalData` entry comes from that export (`Core/Seasons/seasonal-data.json`,
     `tools/seasons/gen_seasonal_data.mjs`), added while `Season:Current` is Season 6; one account's values for everyone,
     as the rest of the literal's seasonal data is. With Season 5 current the login is the TS server's, byte for byte.
   Not done: the websocket push (open item 1); the profile lookups carry no `SeasonalData` (open item 3). **When a season after 6 is made
   current:** nothing has data for it; decide then whether ratings carry over, reset, or start with placement, and
   whether ratings get kept per season.
3. **Opening a custom lobby waited 3 seconds with Season 6 current. Fixed 2026-09-30.** With Season 5 current,
   `create_custom_game_lobby` was followed by `PUT /matches/{id}` within 30 ms. With Season 6 current and no Season 6
   entry in the login's profile, the game first asked `PUT /profiles/bulk?...&fields=server_data.SeasonalData.Season:SeasonSix&partial_response=1`
   (sent as GET), got no `SeasonalData` (neither server has ever sent any there), and `PUT /matches/{id}` followed 3.0 s
   later, every time. Since the login's profile carries Season 6 (open item 2), the game no longer asks, and the lobby
   opens in 34 ms. If a later season is made current without a login entry for it, expect the wait again; the profile
   lookups (`ProfilesService`) never carry `SeasonalData`.

## Still tied to Season 5

Everything below names Season 5 in code or data. None of it breaks with Season 6 current (checked in game: login,
rifts, ranked reset notice), but each is a place to look when a screen shows old or missing season data.

| Where | What |
|---|---|
| `Core/Leaderboards/RankedDataService.cs`, TS `handlers/ssc.ts` ranked_data | ranked data keyed `Season:SeasonFive` |
| TS `src/websocket.ts` `FullRankUpdate` (2 places) | same, after ranked matches (open item 1) |
| TS `services/eloService.ts` | rating updates tagged `Season: "Season:SeasonFive"` |
| leaderboards (`LeaderboardService.cs`, TS `leaderboardShow.ts`) | board names `ranked_season5_*` |
| `Core/Access/login-response.json`, `LoginResponse.cs` | `LastRefreshSeason`, the `season5` stat trackers, Season 5 seasonal data |
| TS `data/rankSettings.ts`, hiss (`hiss-amalgamation.json`) | ranked rewards and event data per season |
| TS `data/milestones.ts`, `missionContainers.ts`; `ssc-get-milestone-reward-tracks.json` | Season 5 battle pass, missions and reward tracks |
| `ssc-load-rifts.json`, `ssc-get-hiss-calendar-events.json`, `calendar-events.json` | Season 5 rifts and calendar events |
