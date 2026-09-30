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

"Retry" on the results screen sends `PUT /ssc/invoke/retry_current_rift_node` `{Context: 1, MatchId}` (the match
just played; `Context` is likely an `EMvsRiftRetryContext`; the offline backend, `0x1429c9930`, reads `MatchId`). C# (`RiftMatchService.RetryNodeAsync`) starts the same node again from
the prior match's record and the websocket sends the config as `RiftRetryNotification` `{MatchId, GameplayConfig,
PriorMatchId}`, which is what the offline backend sends instead of `OnGameplayConfigNotified`. Played on the bench
(2026-09-30): the match restarted.

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

**The answer is `{body: {RiftState: <state>}}`**: the game's handler (`0x142a2dcc0`) reads `RiftState` and fails without
it, and the rift page then spins forever. Until 2026-09-30 the state was sent as the body itself (the save cache, which
holds only the state, had suggested that), and the page worked only while the game had an old cached state.

Stored per account in Mongo `riftstates` (`account_id`, `state`, timestamps), created on the first call; only C# reads or
writes it. A new player has nothing played, no daily rewards, the global attrition pool full (stocks and daily attempts
are settings: `Rifts:GlobalAttritionStocks` 6, `Rifts:GlobalDailyAttempts` 5, the values the WB cache shows), and a full
pool for each rift that keeps its own (`Attrition.bTargetRiftPoolInsteadOfGlobalPool`: the tutorial 10, Triple Threat 6,
the rogue rifts 5), in the shape WB wrote for each kind; an existing state gets missing pools when read. `LastPlayed`
follows the last node played (`RiftProgressService`).

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

## Stars: a node's missions

The stars beside a node on the map are its missions at the chosen difficulty: the node's `Missions` entries whose
`ChapterDifficultyThreshold` is that difficulty (five at Easy on the Joker node), from the **hiss** `rift-config`,
which the game shows; the TS `load_rifts` copy lists different ones. Each mission (`missions`) names objectives
(`mission-objectives`) whose `ObjectiveFlags` test the counters the game reports with the result
(`EndOfMatchStats.PlayerMissionUpdates[player]`, 206 of them, e.g. `Stat:Game:Character:TotalRingouts`), the win
(`Objective:Match:Win`), or a tag on the player's skin (`Objective:Match:Tag:Skin`; the tags come from the TS
`INVENTORY_DEFINITIONS`, `Core/Rifts/rift-item-tags.json`). Descriptions and objectives often disagree in the game's
data; the objectives are what counts. Rules in `RiftMissions.cs`.

A win keeps the stars that match earned (the others stay open for another attempt): each is added once to the node's
`PlayerInstanceRuntimeData[rift].RuntimeNodeData[node].CompletedMissions["<difficulty>"]`, and the chapter's
`CauldronsByDifficulty[difficulty].CurrentScore` goes up by one for each new one. In the WB-era cache that score equals
the number of missions completed at that difficulty on every rift that has one (Joker 8, Samurai Jack 4, Rise of
Smith 12). The game is then sent `OnPlayerInstanceUpdated` with `RiftSlug` and `PlayerInstance` (that rift's entry):
its handler (`0x142a27740`) reads the slug first. Without it, the stars were stored but the map did not update.

Not yet: the missions' rewards (the first win's gem lootbox, `Reward`, `ClaimedOneTimeRewardGuidsByDifficulty`), the
cauldron tiers (`claim_cauldron`), and what unlocks a higher difficulty.

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

## The current season

The rift season dropdown lists the seasons up to the current one, which the game learns at login from
`attempt_daily_refresh` (`CurrentSeason`; the TS server always said `Season:SeasonFive`). C# answers it now, the
season from the setting `Season:Current`, `Season:SeasonSix` since 2026-09-30 so the Season 6 rogue rifts show.
Answers still keyed by Season 5 (ranked data, `ranked_season5_*` leaderboards, the login's `season5` trackers,
per-season profile data, missions, milestones) may need filling for Season 6 where the game looks them up by the
current season; switch back with `ovs-ctl settings set Season:Current Season:SeasonFive`. Everything still tied to
Season 5, the end-of-season screen and its open items: `docs/SEASONS.md`.

## Decided

- **No rift ends (2026-09-30).** Every rift with `bRiftHasEndTime` (the nine rogue rifts; no earlier rift has an end
  date) keeps it, `Rifts:EndTimeYears` (20) years later, in `load_rifts` and in the hiss the game downloads
  (`RiftCatalog`). 100 years was tried first; it displayed, but the Season 5 page spun on first open at the same time,
  and the offset was lowered before the real cause was found (the rift state's shape, below), so 100 was never shown
  to be wrong.
- **Season 6's rogue rifts are offered.** The hiss has them and the TS `load_rifts` lacks them; their runtime data is
  generated (`RiftCatalog`; the generator reproduces the Season 5 rogue rifts' WB-era data exactly, save bots WB drew
  from a pool). They are re-runs: each is its Season 5 namesake node for node (maps, enemy sets, stocks, buffs,
  missions, rewards) and chapter for chapter, with new GUIDs (progress kept apart), a new schedule and the Season 6
  tag. The game lists them once Season 6 is current (`Season:Current`, above).
- **Everyone starts rifts from scratch**, and every own-pool rift has its pool from the start.

## TODO

Getting rifts playable without dead ends was the goal so far; these are what is left, with what each needs. Client
addresses are in build f97148ff. The client's offline backend answers most of these calls itself (dispatcher
`0x14299b7d0`, see above): each offline handler is the reference for what the online answer holds.

1. **Attrition (lives) for the rogue rifts** (`RiftType` 4, own pool). Config: `RiftData.Attrition` (`InitialStocks` 5,
   `StockReplenishTimer`, `CostPerRun` 1, `bAllowPurchaseLives`, `bTargetRiftPoolInsteadOfGlobalPool`); the chapter's
   `Attrition.DifficultyModifiedSettings` (`bDoPlayerStocksAndDamageCarryOver`, `bDoEnemyStocksAndDamageCarryOver`,
   `PercentageOfPlayerDamageCarriedIntoNextMatch`); the node's (`bDoEnemyStocksAndDamageCarryOver`,
   `PlayerStocksGrantedOnWin`). State: the pool in `PlayerAttrition[rift]` (`CurrentAttritionStocks`,
   `CurrentAttritionDamage`, `CurrentDailyAttempts`, `PremiumResetAttempts`, `CurrentAttritionRegenTimestamp`), and
   `LastFreeAttritionStockClaimTimestamp`, `PaidStockPurchasesSinceFreeClaim`. Runtime: a node's enemy
   `Stocks`/`StartingDamage` carry over after a loss (`RuntimeNodeData[node].EnemyTeams[i]`, `LastKnownConfiguredStocks`).
   Calls: `mod_player_attrition`, `buy_lives` / `purchase_stocks` (`AmountOfLives`, `CurrencyCost`),
   `reset_challenge_rift`, `rift_reset_all_player_data`; notifications `AttritionLivesRewarded`,
   `RiftRunAttemptsRewarded`. The match: team 0's stocks from the pool are capped by the global rift settings'
   `MaxStocksTakenIntoMatch` (`UMvsRiftGlobalSettingsHsda` +0x80; not in the hiss: read the packaged asset). Reference:
   the offline match-end handler (`0x1429c5720`: win check `0x1429c5a70`, then `0x1429c57c0`, `0x1429c5160`,
   `0x1429c4fb0`), `RiftMatchService` (pool stocks already used when a chapter carries stocks over).
2. **Mission rewards.** A node's `Missions[i].Reward` (`InventoryHsda`, e.g. `lootbox_gems_chaos_easy_first_win`,
   `DirectInventoryItemCount`, `RewardGrantMethod` `DirectInventoryItem`, `RewardGuid`) and
   `OneTimeCompletionRewardsByDifficulty`; claimed ones in the player data's `ClaimedOneTimeRewardGuidsByDifficulty`
   (and `ClaimedBattlepassXp`; a mission's `ScoreContribution` 100 is probably battle pass XP). Granting goes through
   the inventory (the TS `grant_reward`, or the C# inventory once it writes); the game is told with `OnRewardsGranted`
   and, after a match, `EndOfMatchPayload` (`GameplayConfig`, `ClientReturnData`: what the offline handler sends,
   `0x1429c9280`).
3. **Cauldrons.** A chapter's `CauldronsByDifficulty` (config: per difficulty, `Tiers[{Reward, ...}]`; player data:
   `CurrentScore`, which the stars already raise, and `ClaimedTiers`). Calls: `claim_cauldron`,
   `rift_unlock_chapter_cauldron_tiers`.
4. **A chapter's end and higher difficulties.** The end node (`RiftEndNodeData`, its `Dialogue.OnClicked`) most likely
   calls `finish_rift_chapter` (a stub now), which should set the chapter's `bIsChapterComplete` and
   `HighestDifficultyCompleted` (0 on a new chapter: whether it means "none" or "Easy done" is unread; the offline
   handler decides). Difficulties: the chapter's `Difficulty` (`DifficultyScalars`, `DifficultyReleaseData`, timed
   releases), `DifficultyIndexToDifficulty`, missions' `ChapterDifficultyThreshold`; calls `set_chapter_difficulty`,
   `switch_rift_chapters`. The bot difficulty sent is 0, as the offline builder sends it; check at a higher difficulty
   that the client scales the bots itself (`DifficultyScalars`).
5. **Co-op.** A friend joining the rift lobby (`lobby:{id}` `playerIds`, invites, `set_lobby_joinable`, seen after a
   match); `start_rift_node` with two humans (both on team 0, player indexes 0 and 2; the rollback server gets both);
   the `mis_play_coop` star (`Objective:Match:PlayCoOp`). `RiftLobbyService` assumes one player today.
6. **Smaller.** `equip_gems` (`{GemsToEquip: [3 slugs]}`, unanswered; storage, and the gameplay config's per-player
   `Gems`); `select_rift_loadout`, `skip_rift_node`, `reload_rift_lobby`, `complete_rift_missions`; daily rewards
   (`DailyRewards`: WB picked rifts and chapters each day); bonus nodes (`NodeType` 2, no enemy team, bonus maps: they
   get a 1v1 config today); `retry_current_rift_node` is missing from the route map (seen only in a proxy log so far);
   per-season data still keyed by Season 5 (ranked, leaderboards, trackers), filled for Season 6 only if a screen needs
   it.
