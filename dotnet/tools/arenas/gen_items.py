#!/usr/bin/env python3
"""Builds src/OpenVersus.Server.Core/Arenas/arena-items.json, the Arena shop's item catalog, from the game's own item
assets (FModel JSON export of Content/Panda_Main/Arenas/Items, one MvsArenaItemHsda per item).

    gen_items.py <fmodel export root>    (the directory holding MultiVersus/Content)

Per item: slug, rarity (left out of the export when it is the class default: Common), the shop weight (StartingWeight,
plus WeightPerRound for each round after the first) and the cost (CostPerLevel's first entry; left out when it is the
class default, which the export does not hold: then the wiki's cost for its rarity, Common 4, Uncommon 6, Rare 8, Epic 10,
Legendary 12, marked costSource "wiki").
"""
import json, pathlib, sys

WIKI_COST = {"Common": 4, "Uncommon": 6, "Rare": 8, "Epic": 10, "Legendary": 12}
root = pathlib.Path(sys.argv[1]) / "MultiVersus/Content/Panda_Main/Arenas/Items"
items = []
for path in sorted(root.glob("*.json")):
    for obj in json.loads(path.read_text()):
        props = obj.get("Properties") or {}
        if "Slug" not in props:
            continue
        rarity = (props.get("Rarity") or "EArenaItemRarity::Common").split("::")[1]
        costs = props.get("CostPerLevel")
        items.append({
            "slug": props["Slug"],
            "rarity": rarity,
            "startingWeight": props.get("StartingWeight", 0),
            "weightPerRound": props.get("WeightPerRound", 0),
            "cost": costs[0] if costs else WIKI_COST[rarity],
            "costSource": "asset" if costs else "wiki",
        })
out = pathlib.Path(__file__).resolve().parents[2] / "src/OpenVersus.Server.Core/Arenas/arena-items.json"
out.write_text(json.dumps({"_source": "tools/arenas/gen_items.py from the game's MvsArenaItemHsda assets", "items": items}, indent=1) + "\n")
print(f"{len(items)} items -> {out}")
