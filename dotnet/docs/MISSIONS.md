# Missions

Status: **TS parity only.** `get_or_create_mission_object` answers the TS server's fixed object (C#
`Core/Missions/Missions.cs`): one WB account's missions, never updated, and no containers at all unless
`Missions:Enabled` (`MISSIONS_ENABLED` on the TS server; off by default and on prod). `claim_mission_rewards` grants
nothing. No server tracks mission progress. The goal is missions that work again: rolled per player, moved by matches,
claimable. This file is what is known so far and the plan.

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
36 character containers (`miscon_unlockable_c001` ... `c036`, GrantBehavior Unlockable, Count 50 over 5 missions).

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
- The client's mission code references `bIsClaimable` (`0x142920d40`, `0x142928fd1`) beside `MissionGuid`,
  `MissionObjectives`, `Slug`, `Progress`; neither the TS literal nor the WB websocket shapes carry it. Hypothesis: the
  client works out claimability from `Progress` against the mission's `Count` (its `FMvsMission.bIsClaimable`), and the
  server may or may not send the flag; whether those references read or write it is unchecked. The client's states are
  `EMissionClaimState` InProgress, CollectionConstraintBlocked, Claimable, Claimed.
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
1. **Per-player mission object** (C#, Mongo): rolled from HISS for the live containers (calendar-active battle pass and
   events, FTUE, character containers by decision below), `Count` per controller by `GrantBehavior` and `Weight`,
   `bForce` first, refreshed on `RefreshRate` (Daily/Weekly) at a fixed UTC hour, `UsedMissions` kept.
2. **Progress:** from `match:end_of_match_stats` (as rift progress is, a migration bridge), objectives evaluated with
   the rift star rules (`RiftMissions.cs`, after checking them against the client's own `UMvsOfflineObjectiveProcessor`),
   `Count` capped (whether to send `bIsClaimable`: RE its readers first); `EndOfMatchPayload` missions delta and `MissionUpdatesComplete` through
   `ws:send`.
3. **Claims:** `claim_mission_rewards` marks the missions claimed and grants `RewardData`; the answer's shape and what the
   client expects after a claim come from RE of the client's claim callback first.
4. **Reward tracks** (if chosen): per-player battle pass and event tracks, `MissionScore` from claims, and
   `get_milestone_reward_tracks` answering the player's own.
5. `send_frontend_mission_updates` (client objectives) once a body has been seen.

Decisions open (the maintainer's):

- What "working" means: show, progress after a match, claim; rewards with or without battle pass progress (step 4).
- Which containers are live: battle pass for which season (no Season 6 containers exist: re-use Season 5's under a
  moved calendar, as the rifts did?), which events, FTUE for new players only, the 36 character containers (characters
  are already unlocked: show them or not).
- The daily and weekly reset time.

Open items: WB's `claim_mission_rewards` answer; what `ClaimLocks` holds; whether a claimed mission leaves `Missions`
or stays with a claimed state; whether WB rolled the five character missions for every character at once; the
`send_frontend_mission_updates` body; whether raw WB-era websocket captures survive (the types file came from some).
