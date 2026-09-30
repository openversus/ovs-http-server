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
| then | `PUT /ssc/invoke/start_rift_node` | **nobody**: the client waits on "Traversing Rift" |

`start_rift_node` sends `{ChapterId, NodeId, RiftLobbyId, MultiplayParams{MultiplayClusterSlug, MultiplayProfileId
"1252499" (the one-player profile), MultiplayRegionId, MultiplayRegionSearchId}}`: on WB it asked for a Multiplay
dedicated server. Here it has to start a rollback match (below).

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

## Starting a rift match (not built)

A custom lobby with a bot, played on the bench, shows what a match start is (capture `custom-bots-0930.pcapng`). The
TS websocket sends, in order: `GameServerReadyNotification` (`MatchKey`, `MatchID`, `Port`, `IPAddress`),
`matchmaking-complete`, `OnGameplayConfigNotified` (`{MatchId, GameplayConfig}`), `PerksLockedNotification` (the same
config with perks), `game-server-instance-ready`. The rollback server sees only the human players (bots run inside the
clients) and posts the end of the match to `/ovs_end_match`.

The gameplay config already has the rift fields, empty in PvP: `bIsRift`, `RiftNodeId`, `RiftNodeAttunement`,
`TeamData`, `HudSettings`, and per player `Gems`, `Buffs`, `StartingDamage`, `BotDifficultyMin`/`Max`,
`BotBehaviorOverride`. A rift match is that pipeline with them filled from the node, not the custom lobby's settings.
Each match node in `load_rifts` (`RiftMatchNodeData[guid].MatchData`) pre-selects everything: `Map`, `EnemyTeams` (bots
by `CharacterSet`, `NumStocks`, `TeamBuffs`), `FriendlyTeam` (partner bots, if any, stocks and buffs: 1v1, 2v1 with a bot
partner, 1v3 and so on), `WorldBuffs`, `bAllowMapHazards`, `Attunement`, `HudSettings`, `CountdownDisplay`,
`MatchDurationSecondsByDifficulty`, `ForcedBuddyFighter`, `GuestFighter`, `PermittedLoadout`, attrition. The runtime
data holds the bots actually picked from each `CharacterSet` (character and skin).

After the match: `complete_rift_node`, `submit_end_of_match_stats` and `finish_rift_chapter` record progress (none is
answered yet).

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

## Next

1. `start_rift_node`: a match on the custom-lobby pipeline (rollback port, the match in Redis, bot perks locked) with a
   rift gameplay config built from the node's `MatchData` and the lobby's runtime data; the websocket messages above.
2. After the match: `complete_rift_node` / `submit_end_of_match_stats` / `finish_rift_chapter` update the rift state and
   runtime data, which then become per player.
3. `equip_gems`; the Season 5 end dates; co-op (a friend joining the rift lobby).
