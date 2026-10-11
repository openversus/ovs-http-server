using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Arenas;

/// <summary>
/// The Arena's numbers: what the run (ArenaRun) settles with and what the game is told (ArenaConstants,
/// FMvsArenaConstantInfo). WB's server held them and the client never had them (docs/ARENAS.md). R = reconstructed from a
/// source (WB's rules text, the wiki, patch 1.4.1, WB's Developer Series Q&A); P = PLACEHOLDER, no source known.
/// </summary>
public sealed record ArenaRules
{
    public static ArenaRules Default { get; } = new();

    /// <summary>Each team's health at the start ("Each team starts at 100 Health").</summary>
    public int StartingLife { get; init; } = 100;

    /// <summary>R: "We start every player with 10 coins".</summary>
    public int StartingCoins { get; init; } = ArenaState.StartingCoins;

    /// <summary>R: patch notes, "from 4 to 6" (the wiki says 8).</summary>
    public int SelectableCount { get; init; } = ArenaState.SelectableCount;

    /// <summary>R: the wiki, "reroll 3 coins".</summary>
    public int CharacterRerollCost { get; init; } = 3;

    /// <summary>R: the wiki's "2 coins" and the game's text (choosing Random GAINS coins). Not for an absent pick.</summary>
    public int RandomCharacterBonus { get; init; } = 2;

    /// <summary>P.</summary>
    public int ShopRerollCost { get; init; } = ArenaState.ShopRerollCost;

    /// <summary>R: patch 1.4.1, "1 per 6 to start".</summary>
    public int InterestPer { get; init; } = ArenaState.InterestPer;

    /// <summary>R: patch 1.4.1, "more at max (10)".</summary>
    public int MaxInterest { get; init; } = 10;

    /// <summary>P.</summary>
    public int CharacterSelectSeconds { get; init; } = 30;

    /// <summary>P.</summary>
    public int ShopSeconds { get; init; } = ArenaState.ShopSeconds;

    /// <summary>P.</summary>
    public int FaceoffWaitSeconds { get; init; } = ArenaState.FaceoffWaitSeconds;

    /// <summary>R: "Games Are Played Out Over 90 Seconds".</summary>
    public int RoundSeconds { get; init; } = ArenaState.RoundSeconds;

    /// <summary>P: how long past a phase's deadline the server still takes a game's late call before deciding for it.</summary>
    public int GraceSeconds { get; init; } = 5;

    /// <summary>R: the wiki, "winners play up to 13 rounds": the per-round tables' length.</summary>
    public int MaxRounds { get; init; } = 13;

    /// <summary>P: health a losing team loses, by round, rising ("the later the rounds get, the more damage you'll take").</summary>
    public IReadOnlyList<int> HealthRoundValues { get; init; } = [.. Enumerable.Range(0, 13).Select(r => 15 + 5 * r)];

    /// <summary>P.</summary>
    public IReadOnlyList<double> HealthRoundScalars { get; init; } = [.. Enumerable.Repeat(1.0, 13)];

    /// <summary>P: coins every player gets at a round's end.</summary>
    public IReadOnlyList<int> CurrencyPerRound { get; init; } = [.. Enumerable.Repeat(5, 13)];

    /// <summary>P.</summary>
    public int CurrencyForWin { get; init; } = 3;

    /// <summary>P: by streak length (0, 1, 2, 3 or more).</summary>
    public IReadOnlyList<int> CurrencyForWinStreak { get; init; } = [0, 1, 2, 3];

    /// <summary>P.</summary>
    public IReadOnlyList<int> CurrencyForLoseStreak { get; init; } = [0, 1, 2, 3];

    /// <summary>P.</summary>
    public IReadOnlyList<int> CurrencyPerRingout { get; init; } = [.. Enumerable.Repeat(1, 13)];

    /// <summary>R: patch 1.4.1, "1 for level 2, 3 for level 3".</summary>
    public IReadOnlyList<int> ItemXpPerLevel { get; init; } = [1, 3];

    /// <summary>P.</summary>
    public int MaxItemsInInventory { get; init; } = 6;

    /// <summary>R: the wiki, the shop "shows 4 upgrades".</summary>
    public int OffersPerPage { get; init; } = 4;

    /// <summary>P: shop pages made per round; the game's local rerolls walk them.</summary>
    public int ShopPages { get; init; } = 10;

    /// <summary>The health a team losing round <paramref name="round"/> loses.</summary>
    public int LifeLost(int round)
    {
        int i = Math.Clamp(round - 1, 0, HealthRoundValues.Count - 1);
        double scalar = HealthRoundScalars.Count == 0 ? 1.0 : HealthRoundScalars[Math.Min(i, HealthRoundScalars.Count - 1)];
        return (int)Math.Round(HealthRoundValues[i] * scalar);
    }

    /// <summary>Interest on <paramref name="coins"/> left unspent at a round's end.</summary>
    public int Interest(int coins) => Math.Min(coins / Math.Max(1, InterestPer), MaxInterest);

    /// <summary>
    /// FMvsArenaConstantInfo, as the game reads it beside an Arena config (ArenaConstants). Per-round tables are
    /// <see cref="MaxRounds"/> long: the game's local shop indexes them by round.
    /// </summary>
    public JsonObject Constants() => new()
    {
        ["RoundLength"] = RoundSeconds,
        ["HealthRoundValues"] = new JsonArray([.. HealthRoundValues.Select(v => (JsonNode?)v)]),
        ["HealthRoundScalars"] = new JsonArray([.. HealthRoundScalars.Select(v => (JsonNode?)v)]),
        ["CurrencyPerRound"] = new JsonArray([.. CurrencyPerRound.Select(v => (JsonNode?)v)]),
        ["CurrencyForWin"] = CurrencyForWin,
        ["CurrencyForWinStreak"] = new JsonArray([.. CurrencyForWinStreak.Select(v => (JsonNode?)v)]),
        ["CurrencyForLoseStreak"] = new JsonArray([.. CurrencyForLoseStreak.Select(v => (JsonNode?)v)]),
        ["CurrencyPerRingout"] = new JsonArray([.. CurrencyPerRingout.Select(v => (JsonNode?)v)]),
        ["CharacterSelectTime"] = CharacterSelectSeconds,
        ["ShopCharacterRerollCost"] = CharacterRerollCost,
        ["ShopRerollCost"] = ShopRerollCost,
        ["CurrencyForRandomCharacterSelect"] = RandomCharacterBonus,
        ["InterestPer"] = InterestPer,
        ["MaxInterest"] = MaxInterest,
        ["FaceoffWaitTime"] = FaceoffWaitSeconds,
        ["ItemXpPerLevel"] = new JsonArray([.. ItemXpPerLevel.Select(v => (JsonNode?)v)]),
        ["MaxItemsInInventory"] = MaxItemsInInventory,
        // P: weights by rarity per round, rarer later ("Rarer and more powerful items will show up in later rounds"). The
        // server's own pages (ArenaItems) use the items' weights from the client's assets instead.
        ["ShopLevelWeights"] = new JsonArray([.. Enumerable.Range(0, MaxRounds).Select(r => (JsonNode?)new JsonObject
        {
            ["RarityWeight"] = new JsonObject
            {
                ["Common"] = Math.Max(0, 50 - 5 * r), ["Uncommon"] = 30, ["Rare"] = 10 + 3 * r, ["Epic"] = 2 * r, ["Legendary"] = Math.Max(0, r - 4),
            },
        })]),
        // P: the shared pool's copies per rarity ("the shop's inventory is shared between all players").
        ["ItemAmountsByRarity"] = new JsonObject { ["Common"] = 10, ["Uncommon"] = 8, ["Rare"] = 6, ["Epic"] = 4, ["Legendary"] = 2 },
        ["InterestPerBoost"] = new JsonArray(),
    };
}
