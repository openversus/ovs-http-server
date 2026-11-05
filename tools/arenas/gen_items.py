#!/usr/bin/env python3
"""Builds src/OpenVersus.Server.Core/Arenas/arena-items.json, the Arena shop's item catalog, from the game's own item
assets (FModel JSON export of Content/Panda_Main/Arenas/Items, one MvsArenaItemHsda per item).

    gen_items.py <fmodel export root>    (the directory holding MultiVersus/Content)

Per item: slug, gem (the slug of the MvsGemHsda its Gem names: what a match applies, as a player's Gems entry in the
gameplay config), rarity (left out of the export when it is the class default: Common), the shop weight (StartingWeight,
plus WeightPerRound for each round after the first), the cost (the wiki's cost for its rarity: Common 4, Uncommon 6,
Rare 8, Epic 10, Legendary 12) and assetCost (CostPerLevel's first entry, only where the asset sets one; the export does
not hold the class default). The assets' costs run higher than the wiki's and neither is confirmed: the shop charges the
wiki's, and assetCost is kept as the alternative.
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
        # The gem asset the item names: /Game/Panda_Main/Arenas/Items/ArenaGems/Gem_Arena_X.Gem_Arena_X -> its slug.
        gem_path = root / "ArenaGems" / (props["Gem"]["AssetPathName"].rsplit("/", 1)[1].split(".")[0] + ".json")
        gem = next(o["Properties"]["Slug"] for o in json.loads(gem_path.read_text()) if "Slug" in (o.get("Properties") or {}))
        items.append({
            "slug": props["Slug"],
            "gem": gem,
            "rarity": rarity,
            "startingWeight": props.get("StartingWeight", 0),
            "weightPerRound": props.get("WeightPerRound", 0),
            "cost": WIKI_COST[rarity],
            **({"assetCost": costs[0]} if costs else {}),
        })
out = pathlib.Path(__file__).resolve().parents[2] / "src/OpenVersus.Server.Core/Arenas/arena-items.json"
out.write_text(json.dumps({"_source": "tools/arenas/gen_items.py from the game's MvsArenaItemHsda assets", "items": items}, indent=1) + "\n")
print(f"{len(items)} items -> {out}")
