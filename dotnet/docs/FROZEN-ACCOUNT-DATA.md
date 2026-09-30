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
  player's inventory and purchases, per request. Ownership comes from `IInventoryService` (`Core/Inventory/`, the
  `/profiles/{id}/inventory` answer: today every enabled asset, perk and taunt, for everyone), so the store and the
  inventory agree. Purchase history has no source yet.

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

### Rifts: `GET /ssc/invoke/load_rifts`

- **Served by:** `GetLoadRifts` (`Http/Endpoints/Game/Ssc/`): `RiftConfigs` from `Static/ssc-load-rifts.json` as
  generated from `handlers/ssc.ts`, the same for everyone, and the player's own runtime data
  (`RiftProgressService`, `Core/Rifts/`, Mongo `riftinstances`); registered by `RiftHosting`. `create_rift_lobby`
  (the lobby's `RuntimeData`) and `start_rift_node` (the node's bots) read that same per-player data (registered too).
- **Classified in:** `docs/fields/load-rifts.json`, compared with the runtime data the game cached for another account
  on WB's servers.
- **Frozen account data in it:** a player's first copy of `DynamicInstanceRuntimeData` is the file's, with every
  chapter's progress cleared (the file's held one account's two finished tutorial nodes; everyone starts from
  scratch). What stays frozen is each node's generated enemy teams (`RuntimeNodeData`), one account's, the same for
  every new player. `PlayerInstanceRuntimeData` starts as the file's, a new player's (all empty). `RiftConfigs` is static.
  The four Season 6 rogue rifts the file lacks get generated runtime data (`RiftCatalog`), their bots the characters and
  skins WB picked for the same character sets on the file's rifts.
- **Becomes:** enemy teams generated per player from each node's `CharacterSet`s, as WB's server did.

## Adding one

A new response that carries captured account data gets a manifest in `docs/fields/`, a test in `FieldManifestTests`,
a `FrozenAccountData` registration (and its line in `FrozenAccountDataTests`), and an entry here.
