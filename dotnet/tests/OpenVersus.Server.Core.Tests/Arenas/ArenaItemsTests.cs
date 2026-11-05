using OpenVersus.Server.Core.Arenas;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>The Arena shop's item catalog (arena-items.json). Needs nothing.</summary>
public sealed class ArenaItemsTests
{
    [Fact]
    // Every item costs the wiki's price for its rarity; the asset's own cost is kept beside it where the asset sets one.
    public void EveryItemCostsTheWikiPriceForItsRarity()
    {
        var wiki = new Dictionary<string, int> { ["Common"] = 4, ["Uncommon"] = 6, ["Rare"] = 8, ["Epic"] = 10, ["Legendary"] = 12 };

        Assert.Equal(44, ArenaItems.All.Count);
        Assert.All(ArenaItems.All, i => Assert.Equal(wiki[i.Rarity], i.Cost));
        Assert.Equal(20, ArenaItems.All.Count(i => i.AssetCost is not null));
        Assert.Equal(14, ArenaItems.Of("arenaitem_airspecialist")!.AssetCost);
        Assert.Null(ArenaItems.Of("arenaitem_andanotherone")!.AssetCost);
    }
}
