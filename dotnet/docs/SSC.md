# SSC functions: notes and follow-ups

The SSC functions (`/ssc/invoke/<name>`) ported to C# so far answer as the TS server does. Several of those answers are
placeholders the TS server never finished: they drop what the game sends. This file lists what each one receives, what
it answers, and what is left to do. Every endpoint listed here points back to this file.

## Follow-ups: the fixed answers (ported 2026-09-30)

Ported answering exactly what the TS server answers (`tools/ssc/constants_diff.mjs`, byte for byte), and nothing is
written. Each needs a closer look before the TS server goes.

| Function | The game sends | Answered with | To do |
|---|---|---|---|
| `PUT game_install` | not captured yet; carries the EULA acceptance date, among others. The C# endpoint logs the body (`game_install body: ...`) | `{body: {}}` | **Store it** (per account): the EULA acceptance and whatever else it carries. Read the logged body first, and find out when the game sends it (each launch, or once). |
| `PUT game_launch_event` | `{account_platform_id, hydra_public_id, is_production, os, platform_name, resolution, user_name}` (11 captures) | status 200, `text/html`, no body (the TS server's `res.send("")`), even to a Hydra request | Check what the game expects back (the WB answer was never captured). Decide what to keep (platform, `hydra_public_id`, last launch). |
| `POST claim_mission_rewards` | `{ContainerSlug, MissionsToClaim: [{MissionControllerSlug, MissionGuid, MissionSlug}]}` (3 captures, weekly battle pass missions) | `{MissionControllerContainers: {}, ClaimLocks: {}}` (as every captured answer) | Nothing is granted. Belongs with missions (`get_or_create_mission_object`) and the rift mission rewards (`RIFTS.md`). |
| `PUT cancel_party_invite` | not captured | `{body: {}}` | Find out what the game expects to happen (the invitee told? the invite withdrawn from the lobby?). |
| `PUT decline_party_invite` | not captured | `{body: {}}` | Same: the inviter is never told. |
| `PUT update_party_game_modes` | not captured | `{body: {}}` | The party's chosen modes are not kept; check whether matchmaking or the lobby should use them. |

## Other notes

- `PUT ranked_claim_end_of_season_rewards` (`{"Season": ...}`) records the claim per player; see `SEASONS.md`.
- The six cosmetics writes (`equip_*`, `set_profile_icon`): `tools/cosmetics/equip_diff.mjs`; the TS websocket's second
  cache `connections:{id}:cosmetics` is not refreshed by them (nor was it by the TS handlers), so a change reaches
  matches only after the next login.
