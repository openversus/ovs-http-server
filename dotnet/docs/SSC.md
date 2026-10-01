# SSC functions: notes and follow-ups

The SSC functions (`/ssc/invoke/<name>`) ported to C# so far answer as the TS server does. Several of those answers are
placeholders the TS server never finished: they drop what the game sends. This file lists what each one receives, what
it answers, and what is left to do. Every endpoint listed here points back to this file.

## Follow-ups: the fixed answers (ported 2026-09-30)

Ported answering exactly what the TS server answers (`tools/ssc/constants_diff.mjs`, byte for byte), and nothing is
written. Each needs a closer look before the TS server goes.

| Function | The game sends | Answered with | To do |
|---|---|---|---|
| `PUT game_install` | not captured yet; carries the EULA acceptance date, among others. The C# endpoint logs the body (`game_install body: ...`) | `{body: {}}` | **Store it** (per account): the EULA acceptance and whatever else it carries. Read the logged body first. Sent only when the game starts without its local settings/save folder (a new install, or that folder deleted), not at every login. |
| `PUT game_launch_event` | `{account_platform_id, hydra_public_id, is_production, os, platform_name, resolution, user_name}` (11 captures) | status 200, `text/html`, no body (the TS server's `res.send("")`), even to a Hydra request | Check what the game expects back (the WB answer was never captured). Decide what to keep (platform, `hydra_public_id`, last launch). |
| `POST claim_mission_rewards` | `{ContainerSlug, MissionsToClaim: [{MissionControllerSlug, MissionGuid, MissionSlug}]}` (3 captures, weekly battle pass missions) | `{MissionControllerContainers: {}, ClaimLocks: {}}` (as every captured answer) | Nothing is granted. Belongs with missions (plan: `MISSIONS.md`) and the rift mission rewards (`RIFTS.md`). |
| `PUT cancel_party_invite` | not captured | `{body: {}}` | Find out what the game expects to happen (the invitee told? the invite withdrawn from the lobby?). |
| `PUT decline_party_invite` | not captured | `{body: {}}` | Same: the inviter is never told. |
| `PUT perks_absent` | `{ContainerMatchId}` (3 captures, during a match's perk selection) | `{body: {message: "Early absent report"}, return_code: 2}` (as every captured answer) | The TS server's fixed answer; find out what an absent report should do (a player who never picked perks?) and what the game does with return code 2. Part of the match flow (`perks_lock`). |
| `PUT update_party_game_modes` | not captured | `{body: {}}` | The party's chosen modes are not kept; check whether matchmaking or the lobby should use them. |
| `PUT set_lobby_joinable` | `{LobbyId, ...}` (5 captures) | `{body: {}}` | Nothing is kept, as on the TS server, where `router.ts` answers first and shadows a second handler in `ssc/routes.ts` that would set the lobby's `joinable` back to true. `set_lobby_not_joinable` sets it false; neither server reads it. |
| `PUT autoparty_join` | not captured | `{body: {}}` | The game's auto-party is not a feature here. |
| `PUT set_mode_for_lobby` (ported 2026-10-01 with the party routes) | `{ModeString, ...}` | the lobby, or `{body: {}}` | Only the lobby's maker is told (`OnLobbyModeUpdated`); the other party member never hears of a mode change. The TS server did the same. |
| `PUT invite_to_player_lobby` (ported 2026-10-01) | `{InviteeAccountID, LobbyId, IsSpectator, ...}` (not captured; names from the TS code) | `{body: {}}` | An invite with no lobby id is still sent, with an empty `MatchID`, as the TS server sends it. |

## Other notes

- `PUT ranked_claim_end_of_season_rewards` (`{"Season": ...}`) records the claim per player; see `SEASONS.md`.
- The six cosmetics writes (`equip_*`, `set_profile_icon`): `tools/cosmetics/equip_diff.mjs`; the TS websocket's second
  cache `connections:{id}:cosmetics` is not refreshed by them (nor was it by the TS handlers), so a change reaches
  matches only after the next login.
