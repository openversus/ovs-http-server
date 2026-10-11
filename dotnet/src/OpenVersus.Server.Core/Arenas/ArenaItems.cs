using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Arenas;

/// <summary>
/// An Arena shop item, from the game's own assets (arena-items.json, tools/arenas/gen_items.py). <see cref="Gem"/> is the
/// slug of the gem the item is in a match (a player's Gems entry in the gameplay config). <see cref="Cost"/> is the
/// wiki's price for its rarity, what the shop charges; <see cref="AssetCost"/> is the asset's own CostPerLevel, where it
/// sets one (higher than the wiki's, and kept as the alternative while neither is confirmed).
/// </summary>
public sealed record ArenaItem(string Slug, string Gem, string Rarity, int StartingWeight, int WeightPerRound, int Cost, int? AssetCost)
{
    /// <summary>Its shop weight in round <paramref name="round"/>: StartingWeight, plus WeightPerRound for each round after the first.</summary>
    public int Weight(int round) => Math.Max(0, StartingWeight + WeightPerRound * (round - 1));
}

/// <summary>The Arena shop's catalog, and the pages a round's shop offers.</summary>
public static class ArenaItems
{
    private static readonly Lazy<IReadOnlyList<ArenaItem>> s_all = new(() =>
    {
        using var stream = typeof(ArenaItems).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Arenas.arena-items.json")
            ?? throw new InvalidOperationException("arena-items.json is not embedded");
        return [.. JsonNode.Parse(stream)!["items"]!.AsArray().Select(i => new ArenaItem((string)i!["slug"]!, (string)i["gem"]!, (string)i["rarity"]!,
            (int)i["startingWeight"]!, (int)i["weightPerRound"]!, (int)i["cost"]!, (int?)i["assetCost"]))];
    });

    /// <summary>Every item.</summary>
    public static IReadOnlyList<ArenaItem> All => s_all.Value;

    /// <summary>The item named <paramref name="slug"/>, if any.</summary>
    public static ArenaItem? Of(string slug) => All.FirstOrDefault(i => i.Slug == slug);

    /// <summary>
    /// <paramref name="pages"/> shop pages for round <paramref name="round"/>, each <paramref name="perPage"/> different
    /// items drawn by their weight for that round (an item at weight 0 is never offered).
    /// </summary>
    public static List<List<string>> Pages(int round, int pages, int perPage, Random random)
    {
        var offered = All.Where(i => i.Weight(round) > 0).ToList();
        var result = new List<List<string>>();
        for (int p = 0; p < pages; p++)
        {
            var page = new List<string>();
            var left = offered.ToList();
            while (page.Count < perPage && left.Count > 0)
            {
                int total = left.Sum(i => i.Weight(round));
                int roll = random.Next(total);
                var pick = left.First(i => (roll -= i.Weight(round)) < 0);
                page.Add(pick.Slug);
                left.Remove(pick);
            }

            result.Add(page);
        }

        return result;
    }
}
