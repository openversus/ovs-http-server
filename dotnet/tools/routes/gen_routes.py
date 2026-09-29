#!/usr/bin/env python3
"""Builds dotnet/docs/routes.json and dotnet/docs/ROUTES.md: every route the game can call, merged from
four sources, and how each compares with the TypeScript server.

Sources (inputs in tools/routes/sources/, with the scanners that made them; made 2026-09-28 from exe sha256 f97148ff...):
  binary   Hydra SDK route builders (0x144f9f5b0..0x145069300) and the game's own social-layer templates
           (0x140f932a0..0x140fa3780), read with i-can-haz's haz.disasm (route_funcs.py, verbs.py). The
           Hydra builders pass the method to 0x144ffaa40 as its third argument: 0 GET, 1 PUT, 2 POST,
           checked against captured requests.
  capture  every request in 8 captures (6 prod, decrypted with the TLS keylog; 2 local), ids normalized.
  server   every express route registration in src/ (server_routes.txt).
  ssc      /ssc/invoke/<name> names from the server, the captures and the binary (ssc_names.py,
           static_fstrings.py).
"""
import json, re, collections, pathlib

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE / "sources"
DOCS = HERE.parent.parent / "docs"

# (method, template, binary address, note). Templates use {name} for path parameters. A method in
# lowercase-free "GET*" style is written as GET with override=True: the SDK sends it as PUT with
# x-hydra-http-method: GET (seen in captures for the bulk lookups and inventory).
HYDRA = [
    ("POST", "/access", "0x144f9f5b0", "method from capture (no enum call)"),
    ("DELETE", "/access", "0x144fab980", "method from capture (no enum call)"),
    ("GET", "/accounts/bulk", "0x144fd7550", "method inferred: sibling of the {network} variant; sent as PUT + override"),
    ("GET", "/accounts/{network}/bulk", "0x144fd76d0", "sent as PUT + x-hydra-http-method: GET (capture: wb_network)"),
    ("POST", "/matches/matchmaking/request/{id}/cancel", "0x144fd78f0", "method from capture"),
    ("?", "/commerce/purchases/{id}/finalize", "0x144fd91b0", "method not read yet; a bare '/' piece may mean one more segment"),
    ("GET", "/layout/{layout_type}/personalized/{variant}/{id}", "0x144fd9620", ""),
    ("GET", "/commerce/steam/mtx_user_info/{id}", "0x144fd98d0", "id is 'me' in captures"),
    ("PUT", "/matches/{id}/leave", "0x144fd9f20", ""),
    ("GET", "/accounts/{id}", "0x144fda420", "also 0x144fda5f0"),
    ("GET", "/accounts/{id}/{sub}", "0x144fda790", "second segment's name not read yet"),
    ("GET", "/matches/{id}", "0x144fda970", ""),
    ("GET", "/matches/all/{id}", "0x144fdab70", ""),
    ("GET", "/commerce/catalog/{id}/products", "0x144fdb040", ""),
    ("GET", "/commerce/purchases/{id}", "0x144fdb4d0", "id is 'me' in captures"),
    ("?", "/commerce/catalog/{id}/products/{product}/purchase", "0x144fdbda0", "method not read yet; body has price_slug"),
    ("POST", "/matches/matchmaking/{criteria}/request", "0x144fdd630", "criteria e.g. 1v1-retail"),
    ("PUT", "/drives/{id}/sync", "0x144fdd810", "method from capture (id: multiversus)"),
    ("PUT", "/accounts/{id}", "0x144fddd00", "also 0x144fddeb0"),
    ("PUT", "/matches/{id}", "0x144fde060", ""),
    ("PUT", "/accounts/me/relationships/{id}/block", "0x144fe81e0", ""),
    ("GET", "/store/store_products/{id}/my_products", "0x144feb090", "segment order inferred"),
    ("GET", "/accounts/{id}/relationships/followers", "0x144fed800", ""),
    ("PUT", "/accounts/me/relationships/{id}/unblock", "0x144ff4890", ""),
    ("PUT", "/accounts/me/relationships/{id}/unfollow", "0x144ff4a20", ""),
    ("GET", "/arenas/{id}/instances/{instance}/participants/{participant}", "0x1450535f0", ""),
    ("GET", "/arenas/{id}/instances", "0x145053920", ""),
    ("GET", "/arenas/{id}/groups/{group}/participants", "0x145053b10", ""),
    ("PUT", "/batch", "0x145054730", "runs sub-requests"),
    ("GET", "/clans/{id}/{sub}", "0x145059f00", "second segment's name not read yet"),
    ("GET", "/file_storage", "0x14505b5d0", "method from capture"),
    ("GET", "/file_storage/{slug}", "0x14505b5d0", "method from capture"),
    ("GET", "/global_configuration_types/{type}/global_configurations/{id}", "0x14505c830", ""),
    ("GET", "/global_configuration_types/{type}/global_configurations", "0x14505caf0", ""),
    ("GET", "/profiles/{id}/inventory", "0x14505dbe0", "sent as PUT + x-hydra-http-method: GET"),
    ("PUT", "/profiles/{id}/inventory", "0x14505dd70", "body: modifications. Same wire request as GET /profiles/{id}/inventory (a PUT with x-hydra-http-method: GET); only that header tells them apart"),
    ("?", "/accounts/me/notifications", "0x145060180", "method not read yet"),
    ("?", "/accounts/me/notifications/{id}", "0x145060de0", "method not read yet"),
    ("GET", "/objects/{type}/unique/{id}/{key}", "0x1450615d0", "pieces include a bare '/': one more segment"),
    ("PUT", "/objects/{type}/unique/{id}/{key}/upsert", "0x1450617e0", "pieces include a bare '/': one more segment"),
    ("PUT", "/objects/{type}/unique/{id}/upsert", "0x145061a20", ""),
    ("GET", "/profiles/bulk", "0x145065060", "sent as PUT + x-hydra-http-method: GET"),
    ("GET", "/leaderboards/{id}/score-and-rank/{account}", "0x1450654c0", ""),
    ("GET", "/leaderboards/bulk/score-and-rank/{id}", "0x145065780", "sent as PUT + x-hydra-http-method: GET"),
    ("GET", "/profiles/{id}", "0x145065a80", "also 0x145065c50"),
    ("GET", "/leaderboards/{id}", "0x145065df0", ""),
    ("GET", "/profiles/search_queries/{id}/run", "0x145066420", ""),
    ("GET", "/leaderboards/{id}/show", "0x1450666c0", ""),
    ("GET", "/leaderboards/{id}/around/{account}", "0x1450668b0", ""),
    ("GET", "/leaderboards/{id}/around/me", "0x145066ac0", ""),
    ("GET", "/leaderboards/{id}/friends", "0x145066cb0", ""),
    ("PUT", "/profiles/{id}", "0x145066ea0", ""),
    ("GET", "/seasons/types/{type}/participant_leaderboards/{id}/score-and-rank/{account}", "0x1450679d0", "segment order inferred"),
    ("GET", "/seasons/{id}/instances/{instance}/participants/{participant}", "0x145067d00", ""),
    ("POST", "/virtual_commerce/purchases/{id}/{item}", "0x145069300", "item e.g. toasts_gleamium"),
]
# Fragments the binary holds with no builder found yet; methods and full shapes unknown.
FRAGMENTS = ["/accounts/me/identity", "/accounts/me/link", "/accounts/me/relationships",
             "/accounts/me/notifications/bulk/{id}", "/configuration/sdk", "/commerce/sales",
             "/relationships/followers"]

# The game's own social layer (UTF-16 templates). Methods from captures, else from the server, else unknown.
SOCIAL = [
    ("friends/me/invitations/{id}/accept", "0x140f932a0"), ("social/me/block/{id}", "0x140f93530"),
    ("friends/me/invitations/{id}/cancel", "0x140f943c0"), ("friends/me/invitations/{id}/decline", "0x140f97d00"),
    ("sessions/device", "0x140f99970"), ("social/me/blocked", "0x140f99e00"),
    ("friends/me/invitations/incoming", "0x140f99fb0"), ("friends/me/invitations/outgoing", "0x140f9a160"),
    ("friends/me", "0x140f9a310"), ("realtime/config", "0x140f9abc0"),
    ("friends/me/invitations", "0x140f9b5e0"), ("accounts/me", "0x140f9b8d0"),
    ("sessions/auth/password", "0x140f9baf0"), ("sessions/auth/token", "0x140f9be40"),
    ("accounts/me/age_information", "0x140fa1bd0"), ("social/me/unblock/{id}", "0x140fa3540"),
    ("friends/me/unfriend/{id}", "0x140fa3780"),
]

# SSC function names the binary holds on an SSC call path (ssc_names.py, verb-shaped ones only), and
# the probable ones from static FString initializers (static_fstrings.py), confirmed as game terms.
SSC_BINARY = """activate_timed_boost add_custom_game_bot cancel_party_invite check_leaver_punishment check_server_grants
check_training_server_ready claim_competition_points claim_server_grants claim_voting_competition_rewards
consume_character_xp_boost convert_candy_to_gold create_party debug_lock_inventory_item debug_unlock_inventory_item
decline_party_invite deduct_guild_dungeon_ticket follow_account game_install game_launch_event get_active_ranked_seasons
get_country_code get_current_ftue_step get_gm_leaderboards get_hiss_calendar_events get_or_create_my_match_config
get_preferred_currency grant_character_gift grant_currency grant_gold invite_to_party join_party join_voting_competition
kick_from_lobby leave_party notify_changing_modes post_login_bonuses promote_to_lobby_leader read_cached_configs
reset_inventory save_current_ftue_step send_frontend_mission_updates send_profile_notification
set_enabled_maps_for_custom_game set_game_mode_for_custom_game set_joinable set_player_handicap_for_custom_game
set_world_buffs_for_custom_game switch_custom_game_lobby_team sync_match_config unlock_ftue_character
update_custom_game_bot_fighter update_int_setting_for_custom_game update_member_data update_party_game_modes
update_team_style_for_custom_game zd_ticket_submit ingame_purchase_event dlc_event consumable_event bot_queue
casual_queue""".split()
SSC_PROBABLE = """claim_all_milestone_reward_track_tiers claim_cauldron claim_milestone_reward_track_tiers equip_announcer_pack
equip_banner equip_gems equip_profile_icon equip_ringout_vfx equip_stat_tracker equip_taunt finish_rift_chapter
get_equipped_cosmetics get_milestone_reward_tracks load_gameplay_config load_rifts local_leaderboard_claim_rewards
local_leaderboard_has_unclaimed_rewards purchase_stocks rift_reset_all_chapters rift_reset_all_player_data
rift_unlock_chapter_cauldron_tiers select_rift_loadout set_chapter_difficulty skip_rift_node start_rift_node
upgrade_track_to_premium""".split()


EXE = pathlib.Path.home() / ".local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe"

# Unreal's own routes (engine, not Hydra): the DataRouter string is in the exe as UTF-16.
ENGINE = [("POST", "/datarouter/api/v1/public/data/clients", "string", "Unreal DataRouter (engine telemetry); exe holds 'datarouter/api/v1/public/data?SessionID='")]

# AccelByte: the Custom Lobbies use it and it is live, though no AccelByte string was
# found in the exe or its DLLs; these routes come from the TS server.
ACCELBYTE = re.compile(r"^/(iam|basic|lobby|agreement|platform|social)(/|\*|$)")
# ...except the game's own social layer: /social/me/* goes to the OpenVersus host with the Hydra token (captured).
SOCIAL_LAYER = re.compile(r"^/social/me/")
# OpenVersus's own routes, by caller (checked against the handlers' comments and the rollback server's source,
# 2026-09-28). Order matters: the first match wins.
OVS_KINDS = [
    ("ovs-client", re.compile(r"^/(api/identify|ovs/client-version|ovs/notifications|ovs/friends|ovs/all-players)(/|$)")),
    ("ovs-rollback", re.compile(r"^/(ovs_register|ovs_match_started|ovs_end_match|ovs_match_status|api/ovs_match_status|mvsi_register|mvsi_end_match)$")),
    ("ovs-admin", re.compile(r"^/(admin|api/admin|api/testing|syncAsset)(/|$)")),
    ("ovs-web", re.compile(r"^/(matches$|api/matches|stats|leaderboard$|api/leaderboard|namechange|account/|home|theme\.|favicon|images/|assets/)")),
]
KIND_TITLES = {
    "ovs-client": "OpenVersus client mod",
    "ovs-rollback": "Rollback server",
    "ovs-web": "Website (browser)",
    "ovs-admin": "Admin, testing and data sync",
}


def exe_has(text):
    if not EXE.exists():
        return False
    data = exe_has.data = getattr(exe_has, "data", None) or EXE.read_bytes()
    return (b"\0" + text.encode() + b"\0") in data or (b"\0\0" + text.encode("utf-16-le") + b"\0\0") in data


def to_regex(template):
    return re.compile("^" + re.sub(r"\\\{[^}]+\\\}", "[^/]+", re.escape(template)) + "$")


def load_observed():
    rows = []
    for line in (SRC / "observed.txt").read_text().splitlines():
        m, u, n = line.split("\t")
        rows.append((m, u, int(n)))
    return rows


def load_server():
    rows = []
    for line in (SRC / "server_routes.txt").read_text().splitlines():
        m, p, _ = line.split("\t")
        rows.append((m, re.sub(r":([A-Za-z0-9_]+)", r"{\1}", p)))
    return rows


# Hosts and paths in the captures that are not the game talking to the OpenVersus server.
NOT_GAME = re.compile(r"^/(appinfo|dynamicstore|events/|textfilter|dns-query|dokken/|v1/fleets|\{n\}$|$|//|[0-9a-f]{40})")


def area_of(path):
    seg = path.strip("/").split("/")[0]
    return seg or "root"


def main():
    routes = collections.OrderedDict()

    def add(method, path, **src):
        key = (method, path)
        r = routes.setdefault(key, {"method": method, "path": path, "area": area_of(path), "binary": None,
                                    "capture": 0, "server": [], "notes": []})
        if src.get("binary"): r["binary"] = src["binary"]
        if src.get("note"): r["notes"].append(src["note"])
        return r

    for method, path, addr, note in HYDRA + ENGINE:
        add(method, path, binary=addr, note=note)
    observed = load_observed()
    server = load_server()
    server_by_path = collections.defaultdict(set)
    for m, p in server:
        server_by_path[p].add(m)
    for path, addr in SOCIAL:
        path = "/" + path
        methods = {m for m, u, n in observed if to_regex(path).match(u)}
        src = "capture"
        if not methods:
            methods = {m for m, p in server if to_regex(path).match(p) or p == path}
            src = "server"
        if not methods:
            methods = {"?"}
            src = "unknown"
        for m in methods:
            add(m, path, binary=addr, note=f"social layer; method from {src}")

    # SSC: one route per name; method from captures, else server, else PUT-or-unknown.
    ssc_methods = collections.defaultdict(set)
    for m, u, n in observed:
        if u.startswith("/ssc/invoke/"): ssc_methods[u[12:]].add(m)
    for m, p in server:
        if p.startswith("/ssc/invoke/"): ssc_methods[p[12:]].add(m)
    ssc_names = set(ssc_methods) | set(SSC_BINARY) | set(SSC_PROBABLE)
    for name in sorted(ssc_names):
        how = "binary" if name in SSC_BINARY else "binary (probable)" if name in SSC_PROBABLE else "server/capture"
        in_exe = name in SSC_BINARY or name in SSC_PROBABLE or exe_has(name)
        if not in_exe and name in ssc_methods:
            how += "; not a whole string in the exe (likely built inline); the server implements it because the game calls it"
            in_exe = True
        for m in sorted(ssc_methods.get(name) or {"?"}):
            r = add(m, f"/ssc/invoke/{name}", binary="ssc name" if in_exe else None, note=f"ssc: {how}")
            r["area"] = "ssc"

    for f in FRAGMENTS:
        if not any(r["path"] == f for r in routes.values()):
            add("?", f, binary="fragment", note="binary fragment; no builder found yet")

    # Attach captures and server routes to the templates they match; anything left becomes its own row.
    templates = list(routes.values())

    def literal_count(path):
        return sum(1 for seg in path.split("/") if seg and not seg.startswith("{"))

    def overridden_get(r):
        return r["method"] == "GET" and any("x-hydra-http-method" in n or "PUT + override" in n for n in r["notes"])

    def best(method, path):
        hits = [r for r in templates if r["method"] in (method, "?") and (r["path"] == path or to_regex(r["path"]).match(path))]
        if method == "PUT":
            hits += [r for r in templates if overridden_get(r) and (r["path"] == path or to_regex(r["path"]).match(path))]
        if not hits:
            return []
        top = max(literal_count(r["path"]) for r in hits)
        return [r for r in hits if literal_count(r["path"]) == top]

    for m, u, n in observed:
        if NOT_GAME.match(u): continue
        hit = best(m, u)
        for r in hit: r["capture"] += n
        if not hit:
            add(m, u, note="capture only")["capture"] += n
    for m, p in server:
        if m == "USE": continue
        hit = best(m, p) if m != "ALL" else []
        for r in hit: r["server"].append(f"{m} {p}")
        if not hit:
            add(m, p, note="server only")["server"].append(f"{m} {p}")

    rows = sorted(routes.values(), key=lambda r: (r["area"], r["path"], r["method"]))
    for r in rows:
        r["kind"] = "accelbyte" if ACCELBYTE.match(r["path"]) and not SOCIAL_LAYER.match(r["path"]) else next((k for k, rx in OVS_KINDS if rx.match(r["path"])), "game")
        r["in_game"] = r["kind"] in ("game", "accelbyte") and (bool(r["binary"]) or r["capture"] > 0 or r["kind"] == "accelbyte" or r["path"].endswith("/access"))
        r["in_server"] = bool(r["server"])
    DOCS.mkdir(exist_ok=True)
    (DOCS / "routes.json").write_text(json.dumps(rows, indent=2) + "\n")
    write_markdown(rows)
    game = [r for r in rows if r["in_game"]]
    print(f"{len(rows)} routes: {len(game)} the game can call, {sum(r['in_server'] for r in game)} of them answered by the TS server; "
          f"{sum(1 for r in rows if r['in_server'] and not r['in_game'])} server-only")


def write_markdown(rows):
    def src(r):
        s = []
        if r["binary"]: s.append(f"binary `{r['binary']}`")
        if r["capture"]: s.append(f"capture ×{r['capture']}")
        return ", ".join(s) or "—"

    def line(r):
        server = "yes" if r["in_server"] else "**no**"
        notes = "; ".join(dict.fromkeys(n for n in r["notes"] if n))
        return f"| {r['method']} | `{r['path']}` | {src(r)} | {server} | {notes} |"

    game = [r for r in rows if r["in_game"] and r["kind"] == "game"]
    ssc = [r for r in rows if r["area"] == "ssc"]
    missing = [r for r in game if not r["in_server"] and r["area"] != "ssc"]
    ssc_missing = [r for r in ssc if not r["in_server"]]
    ovs = {k: [r for r in rows if r["kind"] == k] for k in KIND_TITLES}
    accelbyte = [r for r in rows if r["kind"] == "accelbyte"]
    head = "| Method | Route | Game source | TS server | Notes |\n|---|---|---|---|---|"
    out = [
        "# Routes",
        "",
        "Every route the game can call, from its binary and from captured traffic, and whether the TypeScript",
        "server answers it; then the server's own routes. Generated by `tools/routes/gen_routes.py` (inputs and the scanners that made them in",
        "`tools/routes/sources/`) into `routes.json` and this file; edit the generator, not these.",
        "Binary addresses are for the game's only build (exe sha256 `f97148ff…`).",
        "",
        "**Methods.** The Hydra SDK passes the method to `0x144ffaa40` as a number: 0 GET, 1 PUT, 2 POST (checked",
        "against captures). Some GETs travel as `PUT` with `x-hydra-http-method: GET` (the bulk lookups and",
        "`/profiles/{id}/inventory`, which also has a real PUT on the same path), so the server must route on the",
        "override header, not on the wire method. `?` means the method is not known yet.",
        "",
        "**SSC.** The game's SSC function names are not all provably enumerable from the binary: names reach",
        "`/ssc/invoke/` by several paths, and some are built inline. The skeleton therefore has a catch-all",
        "`/ssc/invoke/{name}` that logs unknown names, beside one endpoint per name listed here.",
        "",
        "**Websockets** (not in the tables): the Hydra realtime socket (the ws service; binary Hydra messages; the",
        "server pings `0x0c` every 20 s and the game answers `0x0a`), and the AccelByte lobby socket at `/lobby/`",
        "(text `type: …` messages; Custom Lobbies).",
        "",
        f"**Totals.** {len(rows)} routes. The game can call {len(game)} Hydra/engine/social routes and SSC functions; the TS",
        f"server answers {sum(r['in_server'] for r in game)}. Not answered: {len(missing)} routes and {len(ssc_missing)} SSC functions.",
        f"AccelByte: {len(accelbyte)}. OpenVersus's own, not the game: " + ", ".join(f"{KIND_TITLES[k].lower()} {len(v)}" for k, v in ovs.items()) + ".",
        "",
        "Every row in `routes.json` has a `kind`: `game`, `accelbyte`, or one of OpenVersus's own (`ovs-client`,",
        "`ovs-rollback`, `ovs-web`, `ovs-admin`). The skeleton's endpoint folders follow the same split.",
        "",
        "## Game routes the TS server does not answer",
        "", head, *map(line, missing), "",
        "## SSC functions the TS server does not answer",
        "", head, *map(line, ssc_missing), "",
        "## AccelByte routes (Custom Lobbies; from the TS server)",
        "", head, *map(line, accelbyte), "",
        *[x for k, v in ovs.items() for x in (f"## OpenVersus's own: {KIND_TITLES[k]}", "", head, *map(line, v), "")],
        "## All game routes (not SSC)",
        "", head, *map(line, [r for r in game if r["area"] != "ssc"]), "",
        "## All SSC functions",
        "", head, *map(line, ssc), "",
    ]
    (DOCS / "ROUTES.md").write_text("\n".join(out))


if __name__ == "__main__":
    main()
