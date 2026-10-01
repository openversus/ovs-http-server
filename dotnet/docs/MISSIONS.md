# Missions

Status: **missions are rolled per player and moved by matches; claims work but item rewards.** With `Missions:Enabled`
(off by default, as `MISSIONS_ENABLED` is on the TS server and prod), `get_or_create_mission_object` answers the
player's own missions (C# `Core/Missions/MissionService.cs`, Mongo `missionobjects`), rolled from the game data for the
containers in `Missions:Containers` and refreshed at the resets (`Missions:ResetHourUtc`, `ResetMinute`,
`WeeklyResetDay`, which `attempt_daily_refresh` also reports), and the calendar the game is sent keeps those containers'
events running (`Missions:EventEndYears`). Match results move them (step 2). Off, it answers as the TS server does: no
containers. Claims (`claim_mission_rewards`) remove finished missions and add to the container's reward tracks, which
are per player and start from nothing (`RewardTracks:PerPlayer`). This file is what is known so far and the plan.

Sources, each item says which: the game data the servers already send (HISS, `hiss-amalgamation.json`; the calendar,
`Static/ssc-get-hiss-calendar-events.json`), the client binary (build `f97148ff`; headers in the UHT and jmap dumps),
the TS server's literal (copied from a WB account), WB-era websocket messages (`src/interfaces/websocket-cmds_types.ts`,
generated from WB traffic in 2025-03/04: shapes with one example value each, not the traffic itself), and the 11 captured
`get_or_create_mission_object` answers (all TS). No WB-era HTTP answer for a mission call has been found, and none of
the three client save folders holds a mission cache (they do hold `HydraMrtJson.sav`, the reward tracks).

## The game data (HISS)

Five tables, each `{_hydra_compressed: {slug: {data: ...}}}` under `body.Data`:

| table | count | what it holds |
|---|---|---|
| `mission-containers` | 82 | `MvsMissionControllerContainerData`: `Type` (BattlePassDaily, BattlePassWeekly, DailyEvent, WeeklyEvent, Ftue, None), `RefreshRate` (Daily, Weekly, None), `GrantBehavior` (RandomByWeight, DescendingOrderByWeight, Unlockable), `MissionControllers` [slugs], `AssociatedEvent` (a calendar event), `RewardTracksToAdvance` [{RewardTrack, ScoreGrantBehavior MissionScore or Incremental, ContainerOverrideValue}], `bCleanRefresh`, `bEventRefreshCatchup`, UI fields |
| `mission-controlers` (sic) | 101 | `MvsMissionController`: `MissionList`, `Count` (missions active at once), `Constraints`, `UnlockConstraints` (objective slugs) |
| `mission-list` | 103 | `MvsMissionListData.MissionList` [{Mission, Weight, bForce, RewardData [rewards], bClaimAutomatic}] |
| `missions` | 1315 | `MvsMissionData`: `MissionObjectives` [{ObjectivePtr, Count}], `bAllObjectivesMustBeCompleted`, `ProgressConstraints`, `UnlockConstraints`, `ScoreContribution` |
| `mission-objectives` | 431 | `ObjectiveFlags` [{GameplayTag, Operator, Value}]: the same tests the rift stars use (`RiftMissions.cs`) |

Container families: battle pass daily and weekly per season (`miscon_battlepassdaily_s5`, `miscon_battlepassweekly_s5`;
**no Season 6 containers exist**), events (all past), FTUE (`miscon_ftue`: three controllers, 14 + 7 + 7 missions), and
33 character containers (`miscon_unlockable_c001` ... `c036`, with numbers missing and `c023A` and `c023b` both there;
GrantBehavior Unlockable, Count 50 over 5 missions).

Which containers are live is decided by the calendar the servers serve (`get_hiss_calendar_events`, a fixed file):
`evt_battlepass_season_five` runs 2025-02-04 to 2025-05-30, and `evt_season5_arenaevent3` is in it too. Both match the
containers in the TS literal and the WB messages (`miscon_battlepassdaily_s5`, `miscon_battlepassweekly_s5`,
`miscon_event_arenas5-3`). The calendar is ours, so its dates can move, as the rift seasons did (`SEASONS.md`).

## The player's mission object (client, TS literal, WB messages)

```
body: { updated_at, owner_id, unique_key "missions", object_type_slug "player-missions", id, created_at,
        aggregates {}, calculations {}, owner {}, expire_time null, owner_model "account",
        server_data: {
          MissionControllerContainers: {
            <container>: { MissionControllers: {
              <controller>: {
                Missions: [ { <mission slug>: { MissionGuid, MissionObjectives: [{Slug, Progress}], bIsClaimable? } }, ... ],
                UsedMissions: [ <mission slug>, ... ] } } } },
          ClaimLocks: {} } }
```

- Each element of `Missions` is a group: one mission each for battle pass dailies, all five in one group for a character
  container (client `FMvsMissionController.MissionGroups`).
- `bIsClaimable`: both client mission readers (`0x14291ff00` for the object, `0x142928790` for the end-of-match delta)
  test for the field and, when present, read it as a bool (the same has-field / get-field calls as `Progress`). The game
  offers a claim only when it is true: on the bench (2026-10-01) a mission at 400/400 without it was not claimable. So
  a finished mission is answered with `bIsClaimable: true`, worked out at every answer. (WB's object in the TS literal
  has 8 finished, unclaimed weekly missions without it: either the copy dropped it, or WB set it some other way.) The
  client's states are `EMissionClaimState` InProgress, CollectionConstraintBlocked, Claimable, Claimed.
- **The lifecycle, read off the WB object** (strong inference, one account): `UsedMissions` is every mission ever granted
  to the controller, in grant order; `Missions` is the granted ones not yet claimed. Weekly: 48 used, 29 outstanding
  (6 weeks of 8; 19 claimed; 8 of the 29 finished and unclaimed). The FTUE login controller: 7 used, none outstanding.
  A character controller: 5 used, 4 left in its one group. So a claim removes the mission (from its group; an empty
  group goes), and a refresh adds new groups without clearing unfinished ones (`bCleanRefresh` false on every
  container used). The dailies granted 24 in the season's first 6 weeks (7 at most per controller): one roll per day
  the player came, not one per day passed (`bEventRefreshCatchup` false).
- Roll rules, as far as the data shows: `DescendingOrderByWeight` takes the `Count` heaviest list entries and does not
  skip used ones (the weekly history repeats the same slugs week after week); `RandomByWeight` draws `Count` by weight
  (the daily history never repeats within a controller's list until it runs out); `Unlockable` grants the whole list
  once (RefreshRate None).
- `UsedMissions` repeats slugs heavily in the literal. **Hypothesis:** a history of missions granted, used to avoid
  repeats when rolling; not confirmed.
- The HTTP answer writes dates as `{_hydra_unix_date}`; the websocket messages carry the same object with ISO text dates.

## The calls

| call | body | answer | today |
|---|---|---|---|
| `POST get_or_create_mission_object` | `{}` (11 captures) | the object above | C#: fixed object, empty unless `Missions:Enabled` |
| `POST claim_mission_rewards` | `{ContainerSlug, MissionsToClaim: [{MissionControllerSlug, MissionGuid, MissionSlug}]}` (3 captures) | TS: `{MissionControllerContainers: {}, ClaimLocks: {}}`; WB's unknown | C#: that fixed answer |
| `send_frontend_mission_updates` | unknown: client objectives (`UMvsMissionManager.SendMissionUpdatesToHydra`, `SendClientBoolUpdate`, e.g. `Objective.Client.OpenPrestigeStore`, `Objective.Client.Ftue.LevelUpGem`) | unknown | stub, never captured |
| `PUT submit_end_of_match_stats` | `EndOfMatchStats.PlayerMissionUpdates[player]`: counters per tag (e.g. `Stat:Game:Character:TotalRingouts`) | | TS; C# rift progress reads it from `match:end_of_match_stats` |

Websocket (`profile-notification`, WB messages):

- `MissionUpdatesComplete`: `data` is the whole mission object.
- `EndOfMatchPayload`: `ClientReturnData.Missions.PlayerMissionObject.MissionControllerContainers`, only the controllers
  the match changed; beside it `ClientReturnData.MilestoneRewardTracks` {MrtDeltas, ScoreSources} (battle pass, mastery,
  event tracks). The TS websocket sends `ClientReturnData: {}`.
- `OnUnlockedMissionContainer`: handled by the client (`0x140d087d0`), payload unknown (perhaps the
  `ContainerSlug` / `ControllerSlug` / `ControllerData` reader at `0x1429287fb`).
- `OnRewardsGranted` {RewardsGranted: [...]}: how granted rewards reach the client (`UMvsMissionManager.HandleRewardsGranted`).

Client addresses (build `f97148ff`): claim request `0x142924b30`; `get_or_create_mission_object` call and answer
handling `0x142929cf0` / `0x14292a030` (special handling of `miscon_ftue`, `mislis_ftue`, `mislis_ftue_timed`,
`mislis_ftue_daily_logins`, `UsedMissions`); mission object readers `0x14291ff00`, `0x1429238b0`; `EndOfMatchPayload`
reader near `0x14292846b`; `MissionBulkClaimTimeout` `0x14292ad32`; notification dispatch `0x140d08390`.

## What rewards depend on

Almost every container pays out through `RewardTracksToAdvance`: a claimed mission's `ScoreContribution` advances the
season's battle pass track (`mrt_battlepass_season_five`) or the event's track. The servers keep no per-player reward
track state: `get_milestone_reward_tracks` answers the same fixed states to everyone. A list entry's `RewardData`
(items) is the other payout. So working rewards means either missions with item rewards only, or per-player reward
tracks (battle pass progress) as well.

## Plan

0. **Done:** `get_or_create_mission_object` ported at TS parity (`Missions:Enabled`).
1. **Done: per-player mission object** (C#, Mongo `missionobjects`): rolled for the live containers by the rules
   above, rolled again on read after a reset, the calendar's events for those containers moved on. FTUE is left out
   for now (the client handles `miscon_ftue` apart; its login controller is not moved by matches): to add after
   reading what the client does with it. `attempt_daily_refresh`'s `PlayerMissionObject` stays empty (WB's carried
   containers; whether the whole object or what the refresh granted is not known).
2. **Done: progress** from `match:end_of_match_stats` (migration bridge 4, beside rift progress):
   `Core/Missions/MissionProgress.cs`. The match as the game saw it from the TS notification at `{matchId}` (mode, map,
   PvP or not, teams; a C# match's overrides), the character from `rift_match:{match}` or `player:{id}`; objectives
   judged with their flags (counters added with `+=`, the rest as conditions; class and character tags as the rift
   stars judge them), capped at `Count`, once per player and match; the game told with `MissionUpdatesComplete`
   (a `profile-notification`, the whole object) through `ws:send`. Custom games move nothing unless
   `Missions:CustomGamesProgress`. Not in `EndOfMatchPayload` (the TS websocket sends that; `ClientReturnData` stays
   empty).
3. **Done (but item rewards): claims.** `claim_mission_rewards` takes each named mission that is finished out of its
   group and adds to the container's reward tracks (`MissionScore`: the mission's `ScoreContribution`; `Incremental`:
   1 per mission); the answer is the player's `server_data` after it (a hypothesis: the shape of the TS fixed answer, which the client hands to its whole-object routine; for the bench to settle).
   Not yet: a list entry's `RewardData` (items, and reward tables such as `reward_xp_fighter_road_300`, whose amounts
   are client data the servers do not have). Client side for the record: the claim request (`0x142924b30`) registers its
   answer callback through a delegate (vtable `0x146736d40`, thunk `0x142927250`) to `0x1429219b0`, which ends either by
   setting a timer delegate or by calling `0x14292a030`, the routine that handles the whole mission object.
   After a claim the game is sent `RewardTrackStatesUpdated` {`RewardTrackStates` (the changed tracks, as
   `get_milestone_reward_tracks` lists them), `UpdateContext` 2} (a `profile-notification`; the client's router,
   `0x140d06c10`, reads both; `UpdateContext` is `EMvsRewardTrackUpdateContext`: Unknown, RewardTrackClaim,
   MissionClaim, EndOfGameProcessing, DailyLogin, DebugEndpoint, XpReward). Beside it the router takes
   `OnMilestoneRewardTrackTiersClaimed` {`RewardTrackStates`, `RewardsGranted`}.
   Tier claims: `claim_all_milestone_reward_track_tiers` {`TrackSlug`} (one capture; the bench's logged sizes agree)
   marks every reward of the track's completed tiers claimed, answers {`RewardTrackStates`: [the track],
   `RewardsGranted`: []} (WB's answer was never captured: the fields of `OnMilestoneRewardTrackTiersClaimed`, for the
   bench to settle) and pushes `RewardTrackStatesUpdated` (`UpdateContext` 1, RewardTrackClaim). The rewards are not
   granted yet (reward tables: below). `claim_milestone_reward_track_tiers` (one tier) is not built: no body seen.

4. **Reward tracks, started:** `get_milestone_reward_tracks` answers each player's own (C#
   `Core/RewardTracks/RewardTracks.cs`, `RewardTracks:PerPlayer`, on by default): the fixed answer's tracks, every one
   starting at score 0 (character and account levels, battle passes, the missions' bonus tracks), the threshold-0 tiers
   reached as WB counted them and their rewards marked claimed, so nothing is claimable that was not earned (the fixed
   answer offered the daily bonus track's tier 1 to everyone). Stored states (Mongo `rewardtracks`) win; nothing
   writes them yet. Next: `MissionScore` from claims, `Incremental` (+1 per finished mission) for the bonus tracks,
   match XP (`EndOfMatchPayload.ClientReturnData.MilestoneRewardTracks`), and the tier claims
   (`claim_all_milestone_reward_track_tiers`, `claim_milestone_reward_track_tiers`: the TS server answers neither).
5. `send_frontend_mission_updates` (client objectives) once a body has been seen.

Decisions:

- **Decided (2026-10-01):** rewards include per-player battle pass progress: step 4 is in scope. No reward track code
  exists on any published branch (only fixed answers).
- **Decided (2026-10-01):** live containers are the Season 5 battle pass daily and weekly under a moved calendar (as the
  rifts were moved), FTUE, and the 33 character containers. The resets are settings: `Missions:ResetHourUtc`,
  `ResetMinute`, `WeeklyResetDay` (default 11:00 UTC on Tuesdays: a guess from the calendar's 10:55 UTC event starts,
  not a known WB reset).
- **Decided (2026-10-01):** character levels start over at zero for everyone. The character mastery tracks
  (`mrt_mastery_<character>`) are answered at tier 99 for everyone today (the fixed `get_milestone_reward_tracks`
  answer), never earned, so per-player tracks start every character at 0.

The character containers (`miscon_unlockable_c0NN`): GrantBehavior Unlockable, RefreshRate None, the controller
unlocked by owning the character (`UnlockConstraints: misobj_ownsitem_c0NN`), each mission progressing only while that
character is played (`ProgressConstraints: misobj_skintag_fixed_c0NN`), and each paying `reward_xp_fighter_road_300`
(`RewardGrantMethod: RewardTableLookup`: Fighter Road XP, not character mastery). Count 50 over a list of 5: all five
at once.

Blocked on client data (the game's own assets; not in the hiss, `dataassets` or the archive): the reward tables a
`RewardTableLookup` names (`reard_perk_currency_80` (sic), `reward_toast_10`, `reward_xp_fighter_road_300`,
`reward_xp_battlepass_tiny`: the names suggest amounts, the tables hold them; an FModel JSON export of them is coming),
and the match XP sources the mastery
tracks name (`MatchData.MatchXpConfig`: `XPSRC_CharacterMastery`, `XPSRC_AccountMastery`, `XPSRC_SkinMastery`,
`XPSRC_CyberEvent`; WB's one example: 50 for `Eog:Source:PlayMatch` on account and character mastery, 100 on
`MRT_FighterRoadV2`). Character and account levels move only once these are known.

**Decided (2026-10-01): Fighter Road is a dead feature.** Every character is unlocked for every player, and Fighter
Road existed to unlock them. Nothing else reads it (`MRT_FighterRoadV2` takes no match XP and no track forwards to it),
so `reward_xp_fighter_road_*` rewards (the character missions' only payout) grant nothing, by design.

Open items: what reaching a mastery tier again grants to a player who already owns that tier's items; WB's
`claim_mission_rewards` answer; what `ClaimLocks` holds; whether a claimed mission leaves `Missions`
or stays with a claimed state; whether WB rolled the five character missions for every character at once; the
`send_frontend_mission_updates` body; whether raw WB-era websocket captures survive (the types file came from some).
