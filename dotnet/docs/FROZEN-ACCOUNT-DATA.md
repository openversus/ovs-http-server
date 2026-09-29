# Frozen account data

Some responses the TS server sends are literals captured from WB's live servers. They mix three kinds of data:

- **static**: the same for every player (catalog, prices, art, game configuration, server settings);
- **account**: the player's own stored data (inventory, purchases, progress, profile);
- **computed**: derived per request from the player's data or the session (ownership flags, adjusted prices, the
  token, timestamps).

The TS server sends all of it, the account and computed parts included, to every player exactly as captured. The C#
port keeps doing so for now (the game is known to accept it), and keeps it easy to replace part by part: each such
response is classified field by field in `docs/fields/`, and is served through one place that can start computing its
account parts without the endpoints changing.

Each of these responses registers a `FrozenAccountData` entry next to the service that serves it
(`services.AddFrozenAccountData(...)`), and the service logs a `FROZEN ACCOUNT DATA` warning for each at startup. The
entry goes away with the registration when a response starts computing its account parts.

The manifests are checked against the generated files (`FieldManifestTests`): regenerating a file from the TS server
fails the build if its manifest names a field that no longer exists or leaves a new one unclassified. Nothing reads the
manifests at runtime yet; they are the plan for what each field becomes.

## Responses

### Store layouts: `GET /layout/dokken-layout-type/personalized/{variant}/{id}`

- **Served by:** `ILayoutSource` (`Core/Layouts/LayoutSource.cs`). Today `FrozenLayoutSource`: `Static/layout-{variant}.json`
  as generated from `handlers/layout.ts`, the same for everyone, cached per process.
- **Classified in:** `docs/fields/store-layouts.json` (provisional: no live layout response is available to compare
  against).
- **Frozen account data in it:** each store product's `already_owned`, `number_times_purchased`, `is_purchasable`,
  `is_player_purchasable`, `valid_user_segments` and the `already_owned_adjusted_cost` prices; and, for `main-variant`,
  which products are listed at all (chosen per player on live).
- **Becomes:** an `ILayoutSource` that takes the static parts from the same files and fills the account parts from the
  player's inventory and purchases, per request. The ownership rule has to be the inventory's (`/profiles/{id}/inventory`),
  so the store and the inventory agree; port them together.

### Login: `POST /access`

- **Served by:** `LoginResponse` (`Core/Access/`), filling `login-response.json`'s `{{markers}}` and computing the stat
  trackers per request.
- **Classified in:** `docs/fields/login-response.json`, compared with live login responses of two real accounts.
- **Frozen account data in it:** most of `profile` (inventory, seasonal data, match history, perk preferences, level and
  XP, owned-fighter counts) and parts of `account` (linked platforms and their ids, timestamps, flags).
- **Becomes:** markers or computed fields in the same template, each read from the player's records (Mongo) as its source
  in the manifest says.

### SDK token exchange: `POST /sessions/auth/token`

- **Served by:** `SessionTokenResponse` (`Core/Access/`), filling `sessions-auth-token.json`.
- **Not classified yet.** Its `account` object is one captured WB account (id, username, public ids) sent to every player.

## Adding one

A new response that carries captured account data gets a manifest in `docs/fields/`, a test in `FieldManifestTests`,
a `FrozenAccountData` registration (and its line in `FrozenAccountDataTests`), and an entry here.
