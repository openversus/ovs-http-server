using System.Text.Json;
using OpenVersus.Server.Core.Arenas;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>The Arena's state machine (ArenaRun), pure: no stores, no clock. Needs nothing.</summary>
public sealed class ArenaRunTests
{
    private const string Me = "me";
    private static readonly ArenaRules Rules = ArenaRules.Default;
    private static readonly IArenaBotMatches Bots = new RandomArenaBotMatches();

    private static readonly IReadOnlyList<(string Character, string[] Skins)> Roster =
        [.. Enumerable.Range(1, 12).Select(n => ($"character_{n}", new[] { $"skin_{n}_default", $"skin_{n}_gold" }))];

    // Me and a bot on team 0, bots everywhere else.
    private static (ArenaRun Run, ArenaStep Step) Start(Random random) => ArenaRun.Start("arena1",
        [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
            [.. Enumerable.Range(0, 2).Select(i => t == 0 && i == 0 ? (Me, "Me", false) : ($"bot{t}{i}", "Bot", true))])],
        Roster, Rules, random, 1_000);

    [Fact]
    // Sixteen places in eight teams at full health; the player offered fighters with the starting coins; round 1 paired
    // (every team once, each with its opponent kept); the character select open until its deadline.
    public void AnArenaStartsInTheCharacterSelectWithRoundOnePaired()
    {
        var (run, step) = Start(new Random(1));

        Assert.Equal((ArenaPhase.CharacterSelect, 1, 16, 8), (run.Phase, run.Round, run.Players.Count, run.Teams.Count));
        Assert.All(run.Teams, t => Assert.Equal(100, t.Life));
        Assert.Equal((Rules.SelectableCount, Rules.StartingCoins), (run.Players[Me].Pool.Count, run.Players[Me].Coins));
        Assert.Empty(run.Players["bot01"].Pool);
        Assert.Equal(Enumerable.Range(0, 8), run.Pairings.SelectMany(p => new[] { p.A, p.B }).Order());
        Assert.All(run.Teams, t => Assert.Single(t.Opponents));
        Assert.Single(run.Pairings, p => p.HasPlayer);
        Assert.Equal(1_000 + (Rules.CharacterSelectSeconds + Rules.GraceSeconds) * 1000L, run.DeadlineMs);
        Assert.Equal([new SendPhaseConfigs()], step.Effects);
    }

    [Fact]
    // A reroll is a new offer for 3 coins, settled at once; with less than 3 left it is refused.
    public void ACharacterRerollCostsThreeCoinsUntilTheyRunOut()
    {
        var (run, _) = Start(new Random(2));

        Assert.True(run.RerollCharacters(Me, Roster, Rules, new Random(3)).Accepted);
        Assert.Equal((7, 6), (run.Players[Me].Coins, run.Players[Me].Pool.Count));
        run.RerollCharacters(Me, Roster, Rules, new Random(4));
        run.RerollCharacters(Me, Roster, Rules, new Random(5));
        Assert.Equal(1, run.Players[Me].Coins);
        Assert.False(run.RerollCharacters(Me, Roster, Rules, new Random(6)).Accepted);
    }

    [Fact]
    // A pick from the offer (its first skin when none is named) ends the character select when it is the last one: bots
    // take fighters and every player gets this round's shop pages. A fighter not on offer is refused.
    public void TheLastPickOpensTheShop()
    {
        var (run, _) = Start(new Random(7));
        string offered = run.Players[Me].Pool[2];

        Assert.False(run.Select(Me, "character_none", null, false, Roster, Rules, new Random(8), 2_000).Accepted);
        var step = run.Select(Me, offered, null, false, Roster, Rules, new Random(8), 2_000);

        Assert.True(step.Accepted);
        var me = run.Players[Me];
        Assert.Equal((offered, $"skin_{offered.Split('_')[1]}_default"), (me.Character, me.Skin));
        Assert.Empty(me.Pool);
        Assert.Equal(ArenaPhase.Shop, run.Phase);
        Assert.Equal((Rules.ShopPages, Rules.OffersPerPage), (me.ShopPages.Count, me.ShopPages[0].Count));
        Assert.All(run.Players.Values.Where(p => p.IsBot), b => Assert.NotNull(b.Character));
        Assert.Equal([new SendPhaseConfigs()], step.Effects);
    }

    [Fact]
    // Random gains its bonus; an absent player gets one of their offer without it (they did not choose Random).
    public void RandomPaysItsBonusAndAnAbsencePaysNothing()
    {
        var (chosen, _) = Start(new Random(9));
        var offer = chosen.Players[Me].Pool.ToList();
        chosen.Select(Me, null, null, true, Roster, Rules, new Random(10), 2_000);
        Assert.Equal((Rules.StartingCoins + Rules.RandomCharacterBonus, true), (chosen.Players[Me].Coins, chosen.Players[Me].RandomPick));
        Assert.Contains(chosen.Players[Me].Character, offer);

        var (absent, _) = Start(new Random(9));
        absent.SelectAbsent(Me, Roster, Rules, new Random(10), 2_000);
        Assert.Equal((Rules.StartingCoins, false), (absent.Players[Me].Coins, absent.Players[Me].RandomPick));
        Assert.Contains(absent.Players[Me].Character, offer);
    }

    [Fact]
    // The character select's deadline picks for whoever has not; the shop's closes it with nothing bought.
    public void DeadlinesDecideForWhoeverHasNotActed()
    {
        var (run, _) = Start(new Random(11));
        Assert.False(run.Tick(run.DeadlineMs - 1, Roster, Rules, Bots, new Random(12)).Accepted);

        run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(12));
        Assert.Equal(ArenaPhase.Shop, run.Phase);
        Assert.Equal(Rules.StartingCoins, run.Players[Me].Coins);

        var step = run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(13));
        Assert.Equal(ArenaPhase.Match, run.Phase);
        Assert.Empty(run.Players[Me].Inventory);
        Assert.Single(step.Effects.OfType<LaunchPairing>());
    }

    [Fact]
    // The shop's summary settles: rerolls cost ShopRerollCost each, a purchase its cost (a duplicate levels the item),
    // a sale gives its value back. The last shop closed starts the matches: the player's pairing is launched, the bots'
    // are decided at once (their losers lose health).
    public void TheShopSettlesFromItsSummaryAndTheMatchesStart()
    {
        var (run, _) = Start(new Random(14));
        run.Select(Me, run.Players[Me].Pool[0], null, false, Roster, Rules, new Random(15), 2_000);
        var me = run.Players[Me];
        me.Coins = 100;
        me.ShopPages = [["arenaitem_attackpower", "arenaitem_thorns"], ["arenaitem_attackpower"]];
        int attack = ArenaItems.Of("arenaitem_attackpower")!.Cost, thorns = ArenaItems.Of("arenaitem_thorns")!.Cost;

        var step = run.ShopClosed(Me, 1, 2, [new(true, 0, 0), new(true, 1, 0), new(true, 0, 1), new(false, 0, 1)], Rules, Bots, new Random(16), 3_000);

        Assert.True(step.Accepted);
        Assert.Equal(100 - 2 * Rules.ShopRerollCost - 2 * attack - thorns + thorns / 2, me.Coins);
        Assert.Equal([("arenaitem_attackpower", 2)], me.Inventory.Select(i => (i.Slug, i.Level)));
        Assert.Equal(ArenaPhase.Match, run.Phase);
        int mine = Assert.Single(step.Effects.OfType<LaunchPairing>()).Pairing;
        Assert.True(run.Pairings[mine].HasPlayer);
        Assert.All(run.Pairings.Where(p => !p.HasPlayer), p => Assert.NotNull(p.Winner));
        Assert.Equal(3, run.Teams.Count(t => t.Life == 100 - Rules.LifeLost(1)));
        Assert.False(run.ShopClosed(Me, 1, 0, [], Rules, Bots, new Random(16), 3_000).Accepted);
    }

    [Fact]
    // The last result ends the round: coins (interest on what was kept, the round's coins, a win's), round 2 paired, its
    // shop open (no character select). A result for no launched match is refused.
    public void TheLastResultEndsTheRound()
    {
        var (run, _) = Start(new Random(17));
        run.Select(Me, run.Players[Me].Pool[0], null, false, Roster, Rules, new Random(18), 2_000);
        var launch = run.ShopAbsent(Me, 1, Rules, Bots, new Random(19), 3_000).Effects.OfType<LaunchPairing>().Single();
        run.AssignMatch(launch.Pairing, "match1");

        Assert.False(run.MatchResult("match0", 0, Roster, Rules, new Random(20), 3_000).Accepted);
        var step = run.MatchResult("match1", 0, Roster, Rules, new Random(20), 3_000);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
        int kept = Rules.StartingCoins;
        Assert.Equal(kept + Rules.Interest(kept) + Rules.CurrencyPerRound[0] + Rules.CurrencyForWin + Rules.CurrencyForWinStreak[1], run.Players[Me].Coins);
        Assert.All(run.Teams, t => Assert.Equal(2, t.Opponents.Count));
        Assert.Equal([new SendPhaseConfigs()], step.Effects);
    }

    [Fact]
    // Played to its end: each team out gets a final rank, the last one in is 1st, every rank once; an odd number of teams
    // puts one against its mirror.
    public void AnArenaPlayedOutRanksEveryTeamOnce()
    {
        var random = new Random(21);
        var (run, _) = Start(random);
        run.SelectAbsent(Me, Roster, Rules, random, 2_000);
        bool mirrored = false;
        var effects = new List<ArenaEffect>();
        for (int round = 0; round < 100 && run.Phase != ArenaPhase.Ended; round++)
        {
            var launches = run.PlayersIn.Any()
                ? run.ShopAbsent(Me, run.Round, Rules, Bots, random, 3_000).Effects.OfType<LaunchPairing>().ToList()
                : run.Tick(run.DeadlineMs, Roster, Rules, Bots, random).Effects.OfType<LaunchPairing>().ToList();
            mirrored |= run.Pairings.Any(p => p.B == ArenaRun.Mirror);
            foreach (var launch in launches)
            {
                run.AssignMatch(launch.Pairing, $"m{round}");
                effects.AddRange(run.MatchResult($"m{round}", run.Pairings[launch.Pairing].B, Roster, Rules, random, 3_000).Effects);
            }
        }

        Assert.Equal(ArenaPhase.Ended, run.Phase);
        Assert.Equal(Enumerable.Range(1, 8), run.Teams.Select(t => t.FinalRank).Order());
        Assert.True(mirrored);
        Assert.Contains(new ArenaFinished(), effects);
    }

    [Fact]
    // The game's view: the offer only while the player has not picked, the shop pages only in the shop, every team's
    // opponent kept, ArenaId beside ArenaData.
    public void TheGameSeesTheOfferThenTheShop()
    {
        var (run, _) = Start(new Random(22));
        var selecting = ArenaState.ConfigData(run, Me, Rules);
        Assert.Equal(Rules.SelectableCount, selecting["ArenaPlayerInfo"]!["SelectableCharacters"]!.AsArray().Count);
        Assert.Empty(selecting["ArenaPlayerInfo"]!["CurrentShopLocal"]!.AsArray());
        Assert.Equal("arena1", (string?)selecting["ArenaId"]);
        Assert.All(selecting["ArenaData"]!["TeamInfo"]!.AsObject(), t => Assert.Single(t.Value!["RoundOpponents"]!.AsArray()));

        run.SelectAbsent(Me, Roster, Rules, new Random(23), 2_000);
        var shopping = ArenaState.ConfigData(run, Me, Rules);
        Assert.Empty(shopping["ArenaPlayerInfo"]!["SelectableCharacters"]!.AsArray());
        Assert.Equal(Rules.ShopPages, shopping["ArenaPlayerInfo"]!["CurrentShopLocal"]!.AsArray().Count);
        Assert.Equal(Rules.OffersPerPage, shopping["ArenaPlayerInfo"]!["CurrentShop"]!.AsArray().Count);
        Assert.Equal(run.Players[Me].Character, (string?)shopping["ArenaPlayerInfo"]!["Loadout"]!["Character"]);
    }

    [Fact]
    // The worker keeps the run as JSON: a round trip keeps everything.
    public void ARunSurvivesItsJson()
    {
        var (run, _) = Start(new Random(24));
        run.SelectAbsent(Me, Roster, Rules, new Random(25), 2_000);

        string json = JsonSerializer.Serialize(run);
        var back = JsonSerializer.Deserialize<ArenaRun>(json)!;

        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Equal((ArenaPhase.Shop, run.Players[Me].Character), (back.Phase, back.Players[Me].Character));
    }
}
