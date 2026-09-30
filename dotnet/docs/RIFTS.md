# Rifts

Status: **partly working.** On the bench the client gets through the rift select page, the rift lobby, the node map,
character select and the loadout lock, and stops at `start_rift_node`: starting a rift match is not built yet. Rifts
supported online co-op (inviting a friend), so the match runs on the rollback server like any other, not offline.

No WB-era rift traffic has been captured. What follows comes from bench captures of the real client, the client binary
(build `f97148ff`), the game data the TS server already sends, and the rift data the game cached for another account on
WB's servers. Each item says which.

## The calls, in the order the client makes them (bench captures, 2026-09-30)

| step | call | answered by |
|---|---|---|
| login | `GET /ssc/invoke/load_rifts` (in the login batch) | C#: the TS server's fixed answer (frozen account data) |
| "Enter Rifts" | `GET /ssc/invoke/get_or_create_rift_state` | C#: `RiftStateService` |
| "Traverse Rift" | `PUT /ssc/invoke/create_rift_lobby` | C#: `RiftLobbyService` |
| then | `PUT /matches/{rift lobby id}` | C#: the party lobby's solo answer (harmless: the node map loads) |
| gems | `PUT /ssc/invoke/equip_gems` `{GemsToEquip: [3 slugs]}` (3 empty strings on "Auto equip") | **nobody** (gems do not save) |
| "Auto equip & fight!" | `PUT /ssc/invoke/lock_rift_lobby_loadout` | C#: `RiftLobbyService.LockLoadoutAsync` |
| then | `PUT /ssc/invoke/set_ready_for_lobby` | TS (party lobby code) |
| then | `PUT /ssc/invoke/start_rift_node` | C#: `RiftMatchService` (the match reaches the game over the websocket) |
| the match | no `perks_lock`: the rift client never sends it, and the match starts without `PerksLockedNotification` | |
| after it | `PUT /ssc/invoke/submit_end_of_match_stats` (`WinningTeamIndex`, `Score`, mission updates), the rollback server's `/ovs_end_match`, then `PUT /ssc/invoke/set_lobby_joinable`; the client is back on the node map | TS (stats, the match's end); **nothing records rift progress** |

`start_rift_node` sends `{ChapterId, NodeId, RiftLobbyId, MultiplayParams{MultiplayClusterSlug, MultiplayProfileId
"1252499" (the one-player profile), MultiplayRegionId, MultiplayRegionSearchId}}`: on WB it asked for a Multiplay
dedicated server. Here it starts a rollback match (below). The client never calls `complete_rift_node` after a match.

An unanswered call leaves the client waiting with no way out of the menu; the game has to be closed, or its websocket
dropped from the server side.

## Rift state: `get_or_create_rift_state`

The client's `FRiftState`: `LastPlayed` (`ChapterGuid`/`NodeId`/`RiftSlug`), `DailyRewards` (the rifts and chapters with
daily rewards, and the nodes done today), `PlayerAttrition` (per pool: stocks, damage, daily attempts, reset attempts,
regen time), `LastFreeAttritionStockClaimTimestamp`, `PaidStockPurchasesSinceFreeClaim`. The rift select page waits for
it (`OnRiftStateUpdated`). The client keeps the answer between sessions (`SaveGames/HydraRiftStateJson.sav`); the answer
here uses the key names that cache has (`Chapter`, not `ChapterGuid`).

Stored per account in Mongo `riftstates` (`account_id`, `state`, timestamps), created on the first call; only C# reads or
writes it. A new player has nothing played, no daily rewards, and only the global attrition pool, full: stocks and daily
attempts are settings (`Rifts:GlobalAttritionStocks` 6, `Rifts:GlobalDailyAttempts` 5, the values the WB cache shows).
Per-rift pools (rifts whose config has `Attrition.bTargetRiftPoolInsteadOfGlobalPool`) appear only once a rift uses them,
as in that cache.

Not done: daily rewards (WB picked 6 rifts and chapters a day); nothing updates the state yet.

## Rift lobby: `create_rift_lobby` and `lock_rift_lobby_loadout`

Request: the party lobby's fields plus `LobbyTemplate "rift_lobby"`, `RiftConfigSlug`, `ChapterGuid`, `ChapterDifficulty`.

What the answer must hold was read from the client's rift lobby parser (a virtual at `0x1429001f0`): `body.lobby`, and in
it **`RuntimeData`** (the rift's `FMvsRiftRuntimeData`: `RuntimeChapterData`, `RuntimeNodeData`, `Powerups`),
**`RiftConfigSlug`** and **`ChapterGuid`**, each required (without one the parser fails and the client never leaves
"Traversing Rift"), and **`RiftState`**, optional. The rest is `create_party_lobby`'s answer. `ModeString` is sent as
`rift_lobby`, a guess (the parser does not read it).

`RuntimeData` is, for now, the rift's entry in the frozen `load_rifts` data (see `FROZEN-ACCOUNT-DATA.md`), with no
powerups. The Redis lobby records are the party lobby's (see `MIGRATION-BRIDGES.md`, shared data contracts).

`lock_rift_lobby_loadout` sends and gets exactly what the party lobby's `lock_lobby_loadout` does (`{AccountId, Loadout,
bAreAllLoadoutsLocked}`); the three characters the TS server disables get `bAreAllLoadoutsLocked: false` (the TS server
never answers them).

## Starting a rift match: `start_rift_node`

Played on the bench (2026-09-30): the Joker node, a full match against the bot, back to the node map afterwards.

`RiftMatchService` answers an empty success and starts the match through `IMatchLauncher` (`Core/Matches/`), which
does what the TS custom lobby does when its host presses start: a rollback port (the fixed range, or on demand: a
port from `rollback:current_port` and a signed POST to the deploy webhook, as `rollbackService.ts`), `match:{id}`,
the bots' perks locked, the notification stored at `{id}` and published on `match:notifications`, then
`matchmaking:complete`. The TS websocket then sends `GameServerReadyNotification`, `matchmaking-complete` and
`OnGameplayConfigNotified`; the rollback server, registering, triggers `game-server-instance-ready`. The rollback
server sees only the human (bots run inside the client).

The websocket builds a PvP gameplay config; the notification carries `gameplayConfigOverride` and
`playerConfigOverrides`, which it merges over that config before sending (MIGRATION-BRIDGES.md, 2). What they hold is
what the client's own offline backend builds for a rift match (`start_rift_node` at `0x1429ca0f0`, which answers an
empty success and hands the game an `OnGameplayConfigNotified` made by `UMvsRiftGameplayConfigCreator`,
`0x1429cbe50`): `bIsRift`, not PvP, `TargetScoreIsLoss` / `AttributeToVictim`, `TeamData [{TargetScore}, {TargetScore}]`
(each side's stocks), the node's map, attunement, HUD, countdown, world buffs, hazards and duration, `ModeString`
"1v1" or "2v2" (a partner bot), and each bot from the runtime data (character, skin, starting damage) and the node's
`MatchData` (banner, icon, ring-out, behaviour, name, its team's buffs), `BotDifficultyMin`/`Max` 0. The rules, with
their sources, are at the top of `RiftMatchService.cs`.

Team 0's stocks are `FriendlyTeam.NumStocks`, unless the chapter carries player stocks over at the lobby's difficulty
(`bDoPlayerStocksAndDamageCarryOver`, on 9 chapters): then the attrition pool, which the client also caps at the
global rift settings' `MaxStocksTakenIntoMatch`, a value nothing here holds (so uncapped).

## After a rift match: how WB recorded progress

The client does not ask for its progress after a match; the server works it out from the result and pushes it. The
client's notification router (`0x140d08390`) handles these rift templates, each a websocket notification whose data
holds the new value: `OnLobbyRuntimeDataUpdated` (`RuntimeData`: the lobby's rift runtime data, read by
`0x142905d90`), `OnLobbyRiftStateUpdated` (`RiftState`, `0x142905cb0`), `OnPlayerInstanceUpdated`,
`MissionUpdatesComplete`, `AttritionLivesRewarded`, `RiftRunAttemptsRewarded`, `OnSwitchedChapters`,
`RiftRetryNotification`, plus `EndOfMatchPayload` and `OnRewardsGranted`.

A completed node, in the game's WB-era cache (`HydraRiftDynamicInstanceJson`): the chapter's
`RuntimeChapterData[chapter].NodeCompletionsByDifficulty` is `{"<difficulty>": [node GUID, ...]}`, next to
`CurrentDifficulty`, `HighestDifficultyCompleted` and `bIsChapterComplete`.

## The client's offline backend

The final build answers its own SSC calls in offline mode (`UMvsOfflineSscManager`, `UMvsOfflineRiftsManager`, the
offline rift lobby, match and state managers, `UMvsRiftGameplayConfigCreator`). `0x14299b7d0` dispatches 26 call names to
their offline handlers: `grant_reward`, `create_party_lobby`, `leave_player_lobby`, `create_rift_lobby`,
`switch_rift_chapters`, `set_chapter_difficulty`, `lock_rift_lobby_loadout`, `start_rift_node` (`0x1429ca0f0`),
`retry_current_rift_node`, `reset_challenge_rift`, `rift_reset_all_player_data`, `set_ready_for_lobby`,
`set_lobby_joinable`, `set_lobby_not_joinable`, `equip_banner`, `equip_ringout_vfx`, `equip_announcer_pack`,
`equip_taunt`, `equip_gems`, `equip_stat_tracker`, `lock_lobby_loadout`, `perks_set_character_page`,
`set_profile_icon`, `update_player_preferences`, `submit_end_of_match_stats`, `complete_rift_node`. Offline mode cannot
give co-op, but each handler is a reference for what the online answer holds.

The route map (`routes.json`) misses most rift calls although they are plain strings in the binary:
`get_or_create_rift_state`, `create_rift_lobby`, `lock_rift_lobby_loadout`, `complete_rift_node`,
`complete_rift_missions`, `retry_current_rift_node`, `reload_rift_lobby`, `switch_rift_chapters`,
`reset_challenge_rift`, `mod_player_attrition`, `buy_lives`.

## Open

- **Season 5's rift page spins forever** (Seasons 1-4 list fine). Hypothesis, untested: all four Season 5 rifts (and
  Season 4's challenge rift) have `bRiftHasEndTime` with end dates in February-April 2025, so the page finds no current
  rift. Keeping them open is a change to the rift data (a policy decision).
- `equip_gems` (and where equipped gems are stored).
- Daily rewards; per-player runtime data (today one frozen copy).
- Bot difficulty: sent as 0, as the offline creator sends it. The Joker bot played very easily on the first test;
  whether a rift bot's strength comes from elsewhere (the chapter difficulty, `DifficultyScalarsOverride`) is unread.

## Next

1. Progress: per-player runtime data, updated from the match result and pushed with `OnLobbyRuntimeDataUpdated` /
   `OnLobbyRiftStateUpdated`; `load_rifts` then answers each player's own.
2. `equip_gems`; the Season 5 end dates; co-op (a friend joining the rift lobby).
