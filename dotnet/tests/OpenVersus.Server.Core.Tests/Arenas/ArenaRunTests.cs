using System.Text.Json;
using OpenVersus.Server.Core.Arenas;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>The Arena's state machine (ArenaRun), pure: no stores, no clock. Needs nothing.</summary>
public sealed class ArenaRunTests
{
    private const string Me = "me";
    private static readonly ArenaRules Rules = ArenaRules.Default;
    private static readonly IArenaBotMatches Bots = new RandomArenaBotMatches();

    // Every player's game still connected.
    private static readonly IReadOnlySet<string> Connected = new HashSet<string> { Me, "two", "mate" };

    private static readonly IReadOnlyList<(string Character, string[] Skins)> Roster =
        [.. Enumerable.Range(1, 12).Select(n => ($"character_{n}", new[] { $"skin_{n}_default", $"skin_{n}_gold" }))];

    // Me and a bot on team 0, bots everywhere else.
    private static (ArenaRun Run, ArenaStep Step) Start(Random random) => ArenaRun.Start("arena1",
        [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
            [.. Enumerable.Range(0, 2).Select(i => t == 0 && i == 0 ? (Me, "Me", false) : ($"bot{t}{i}", "Bot", true))])],
        Roster, Rules, random, 1_000);

    [Fact]
    // Sixteen places in eight teams at full health; the player offered fighters with the starting coins; round 1 paired
    // (every team once, each with its opponent kept; the player's pairing with its match id and their team on side 0) and
    // the player's match launched; the character select open until its deadline.
    public void AnArenaStartsInTheCharacterSelectWithRoundOnePaired()
    {
        var (run, step) = Start(new Random(1));

        Assert.Equal((ArenaPhase.CharacterSelect, 1, 16, 8), (run.Phase, run.Round, run.Players.Count, run.Teams.Count));
        Assert.All(run.Teams, t => Assert.Equal(100, t.Life));
        Assert.Equal((Rules.SelectableCount, Rules.StartingCoins), (run.Players[Me].Pool.Count, run.Players[Me].Coins));
        Assert.Empty(run.Players["bot01"].Pool);
        Assert.Equal(Enumerable.Range(0, 8), run.Pairings.SelectMany(p => new[] { p.A, p.B }).Order());
        Assert.All(run.Teams, t => Assert.Single(t.Opponents));
        var mine = Assert.Single(run.Pairings, p => p.HasPlayer);
        Assert.Equal((0, 0), (mine.A, mine.TeamOnSide(0)));
        // Whichever place the shuffle gave their team.
        Assert.All(Enumerable.Range(100, 20), seed => Assert.Equal(0, Assert.Single(Start(new Random(seed)).Run.Pairings, p => p.HasPlayer).A));
        Assert.Matches("^[0-9a-f]{24}$", mine.MatchId);
        Assert.All(run.Pairings.Where(p => !p.HasPlayer), p => Assert.Null(p.MatchId));
        Assert.Equal(1_000 + (Rules.CharacterSelectSeconds + Rules.GraceSeconds) * 1000L, run.DeadlineMs);
        Assert.Equal([new LaunchPairing(run.Pairings.IndexOf(mine)), new SendPhaseConfigs()], step.Effects);
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
        Assert.False(run.Tick(run.DeadlineMs - 1, Roster, Rules, Bots, new Random(12), Connected).Accepted);

        run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(12), Connected);
        Assert.Equal(ArenaPhase.Shop, run.Phase);
        Assert.Equal(Rules.StartingCoins, run.Players[Me].Coins);

        var step = run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(13), Connected);
        Assert.Equal(ArenaPhase.Match, run.Phase);
        Assert.Equal(Rules.MaxItemsInInventory, run.Players[Me].Inventory.Count);
        Assert.All(run.Players[Me].Inventory, i => Assert.True(i.IsEmpty));
        Assert.Equal([new SendPhaseConfigs()], step.Effects);
    }

    [Fact]
    // The shop's summary settles: rerolls cost ShopRerollCost each, a purchase its cost (a duplicate levels the item),
    // a sale gives its value back. The last shop closed starts the matches: the player's is played (its node launched at
    // the round's start), the bots' are decided at once (their losers lose health).
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
        Assert.Equal([("arenaitem_attackpower", 2)], me.Inventory.Where(i => !i.IsEmpty).Select(i => (i.Slug, i.Level)));
        Assert.Equal(ArenaPhase.Match, run.Phase);
        Assert.Equal([new SendPhaseConfigs()], step.Effects);
        Assert.Null(Assert.Single(run.Pairings, p => p.HasPlayer).Winner);
        Assert.All(run.Pairings.Where(p => !p.HasPlayer), p => Assert.NotNull(p.Winner));
        Assert.Equal(3, run.Teams.Count(t => t.Life == 100 - Rules.LifeLost(1)));
        Assert.False(run.ShopClosed(Me, 1, 0, [], Rules, Bots, new Random(16), 3_000).Accepted);
    }

    [Fact]
    // The inventory as the game's local shop keeps it: MaxItemsInInventory slots; a purchase fills the first empty slot, a
    // copy of an owned item adds to its slot (experience, sell value); a sale empties its slot in place and pays its sell
    // value; the next purchase fills that slot; a full inventory buys nothing new.
    public void TheInventoryIsTheGamesSlots()
    {
        var (run, _) = Start(new Random(33));
        run.Select(Me, run.Players[Me].Pool[0], null, false, Roster, Rules, new Random(34), 2_000);
        var me = run.Players[Me];
        me.Coins = 1_000;
        string[] slugs = [.. ArenaItems.All.Where(i => i.Rarity == "Common").Take(Rules.MaxItemsInInventory + 1).Select(i => i.Slug)];
        me.ShopPages = [[.. slugs]];
        int cost = ArenaItems.Of(slugs[0])!.Cost;

        run.ShopClosed(Me, 1, 0, [.. Enumerable.Range(0, Rules.MaxItemsInInventory + 1).Select(i => new ArenaTransaction(true, 0, i)),
            new(true, 0, 0), new(false, 0, 1), new(true, 0, Rules.MaxItemsInInventory)], Rules, Bots, new Random(35), 3_000);

        Assert.Equal([slugs[0], slugs[Rules.MaxItemsInInventory], .. slugs[2..Rules.MaxItemsInInventory]], me.Inventory.Select(i => i.Slug));
        Assert.Equal((2, 0, cost / 2 * 2), (me.Inventory[0].Level, me.Inventory[0].Xp, me.Inventory[0].SellValue));
        Assert.Equal(1_000 - cost * (Rules.MaxItemsInInventory + 2) + cost / 2, me.Coins);
    }

    [Fact]
    // The inventory lasts the whole Arena: what round 1's shop bought is there in round 2's shop, and in its config.
    public void TheInventoryLastsTheWholeArena()
    {
        var (run, _) = Start(new Random(36));
        run.Select(Me, run.Players[Me].Pool[0], null, false, Roster, Rules, new Random(37), 2_000);
        var me = run.Players[Me];
        string bought = me.ShopPages[0][0];
        me.Coins = 100;
        run.ShopClosed(Me, 1, 0, [new(true, 0, 0)], Rules, Bots, new Random(38), 3_000);

        run.Checkin(Me, 1, Rules, new Random(39), 3_500);
        run.MatchResult(run.Pairings.Single(p => p.HasPlayer).MatchId!, 0, Connected, Rules, new Random(39), 4_000);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
        Assert.Equal(bought, me.Inventory[0].Slug);
        var slots = ArenaState.ConfigData(run, Me, Rules)["ArenaPlayerInfo"]!["playerData"]!["Inventory"]!.AsArray();
        Assert.Equal((Rules.MaxItemsInInventory, bought), (slots.Count, (string?)slots[0]!["Slug"]));
    }

    [Fact]
    // The last result ends the round: coins (interest on what was kept, the round's coins, a win's); then the run waits for
    // the player's game to check in (until the check-in deadline), and the check-in starts round 2: paired, its player's
    // match launched, its shop open (no character select). A result for no launched match, or a side that is not 0 or 1,
    // is refused.
    public void TheLastResultEndsTheRound()
    {
        var (run, _) = Start(new Random(17));
        run.Select(Me, run.Players[Me].Pool[0], null, false, Roster, Rules, new Random(18), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(19), 3_000);
        string match = run.Pairings.Single(p => p.HasPlayer).MatchId!;

        Assert.False(run.MatchResult("000000000000000000000000", 0, Connected, Rules, new Random(20), 3_000).Accepted);
        Assert.False(run.MatchResult(match, 2, Connected, Rules, new Random(20), 3_000).Accepted);
        var step = run.MatchResult(match, 0, Connected, Rules, new Random(20), 3_000);

        Assert.Equal((ArenaPhase.Results, 1, 3_000 + Rules.CheckinSeconds * 1000L), (run.Phase, run.Round, run.DeadlineMs));
        Assert.Empty(step.Effects);
        int kept = Rules.StartingCoins;
        Assert.Equal(kept + Rules.Interest(kept) + Rules.CurrencyPerRound[0] + Rules.CurrencyForWin + Rules.CurrencyForWinStreak[1], run.Players[Me].Coins);
        Assert.True(run.Awaits(Me, 1));

        step = run.Checkin(Me, 1, Rules, new Random(21), 4_000);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
        Assert.False(run.Awaits(Me, 1));
        Assert.All(run.Teams, t => Assert.Equal(2, t.Opponents.Count));
        int next = run.Pairings.FindIndex(p => p.HasPlayer);
        Assert.NotEqual(match, run.Pairings[next].MatchId);
        Assert.Equal([new LaunchPairing(next), new SendPhaseConfigs()], step.Effects);
        Assert.Equal(4_000 + (Rules.ShopSeconds + Rules.GraceSeconds) * 1000L, run.DeadlineMs);
    }

    [Fact]
    // A game that never checks in (it closed) is not waited for past the check-in deadline: round 2 starts without it.
    public void TheNextRoundStartsAtTheCheckinDeadline()
    {
        var (run, _) = Start(new Random(40));
        run.SelectAbsent(Me, Roster, Rules, new Random(41), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(42), 3_000);
        run.MatchResult(run.Pairings.Single(p => p.HasPlayer).MatchId!, 1, Connected, Rules, new Random(43), 4_000);

        Assert.False(run.Tick(run.DeadlineMs - 1, Roster, Rules, Bots, new Random(44), Connected).Accepted);
        var step = run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(44), Connected);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
        Assert.Contains(new LaunchPairing(run.Pairings.FindIndex(p => p.HasPlayer)), step.Effects);
    }

    [Fact]
    // A check-in counts once per round, for a player still in, for the round they played: a second one, a bot's, or one
    // for another round is refused. One that comes before the round is over is kept; the round's last result then starts
    // the next round at once.
    public void ACheckinCountsOncePerRoundAndIsKeptUntilTheRoundIsOver()
    {
        var (run, _) = Start(new Random(45));
        Assert.False(run.Checkin(Me, 1, Rules, new Random(47), 1_500).Accepted);
        run.SelectAbsent(Me, Roster, Rules, new Random(46), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(47), 3_000);

        Assert.False(run.Checkin("bot01", 1, Rules, new Random(48), 3_500).Accepted);
        Assert.False(run.Checkin(Me, 2, Rules, new Random(48), 3_500).Accepted);
        Assert.True(run.Checkin(Me, 1, Rules, new Random(48), 3_500).Accepted);
        Assert.False(run.Checkin(Me, 1, Rules, new Random(48), 3_600).Accepted);
        Assert.Equal(ArenaPhase.Match, run.Phase);
        Assert.True(run.Awaits(Me, 1));

        run.MatchResult(run.Pairings.Single(p => p.HasPlayer).MatchId!, 0, Connected, Rules, new Random(49), 4_000);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
    }

    [Fact]
    // No report: the players against bots win if still connected. A team of two players is still connected while one is.
    public void WithNoReportThePlayersAgainstBotsWinWhileConnected()
    {
        var (run, _) = ArenaRun.Start("arena1", [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
            [.. Enumerable.Range(0, 2).Select(i => t == 0 ? (i == 0 ? Me : "mate", "P", false) : ($"bot{t}{i}", "Bot", true))])],
            Roster, Rules, new Random(50), 1_000);
        run.SelectAbsent(Me, Roster, Rules, new Random(51), 2_000);
        run.SelectAbsent("mate", Roster, Rules, new Random(51), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(52), 3_000);
        run.ShopAbsent("mate", 1, Rules, Bots, new Random(52), 3_000);
        var mine = run.Pairings.Single(p => p.HasPlayer);

        run.MatchResult(mine.MatchId!, null, new HashSet<string> { "mate" }, Rules, new Random(53), 4_000);

        Assert.Equal((0, 1, 100), (mine.Winner, run.Teams[0].Wins, run.Teams[0].Life));
        Assert.Equal(1, run.Teams[mine.B].Losses);
    }

    [Fact]
    // Game rule: a team whose players' games are all gone is out (health 0, ranked out at the round's end, never paired
    // again); with no report, the bots' side wins and plays on in the Arena.
    public void AGoneTeamIsOutAndTheBotsWinAndPlayOn()
    {
        var (run, _) = Start(new Random(55));
        run.SelectAbsent(Me, Roster, Rules, new Random(56), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(57), 3_000);
        var mine = run.Pairings.Single(p => p.HasPlayer);
        int bots = mine.B;

        run.MatchResult(mine.MatchId!, null, new HashSet<string>(), Rules, new Random(58), 4_000);

        Assert.Equal((bots, 0), (mine.Winner, run.Teams[0].Life));
        Assert.Equal(8, run.Teams[0].FinalRank);
        Assert.Single(run.Teams[0].Opponents);
        // No player left in: the bots' Arena is decided to its end at once, the winning bots playing on in it.
        Assert.Equal(ArenaPhase.Ended, run.Phase);
        Assert.True(run.Teams[bots].Opponents.Count > 1);
        Assert.DoesNotContain(0, run.Teams.Where(t => t.Index != 0).SelectMany(t => t.Opponents.Skip(1)));
    }

    // Me (team 0) and "two" (team 1) paired against each other in round 1, bots everywhere else.
    private static ArenaRun TwoPlayersPaired()
    {
        for (int seed = 0; ; seed++)
        {
            var (run, _) = ArenaRun.Start("arena1", [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
                [.. Enumerable.Range(0, 2).Select(i => t < 2 && i == 0 ? (t == 0 ? Me : "two", "P", false) : ($"bot{t}{i}", "Bot", true))])],
                Roster, Rules, new Random(seed), 1_000);
            if (run.Pairings.Any(p => (p.A, p.B) is (0, 1) or (1, 0)))
            {
                run.SelectAbsent(Me, Roster, Rules, new Random(seed), 2_000);
                run.SelectAbsent("two", Roster, Rules, new Random(seed), 2_000);
                run.ShopAbsent(Me, 1, Rules, Bots, new Random(seed), 3_000);
                run.ShopAbsent("two", 1, Rules, Bots, new Random(seed), 3_000);
                return run;
            }
        }
    }

    [Fact]
    // Game rule: players on both sides and no report: both teams take a loss.
    public void WithPlayersOnBothSidesAndNoReportBothLose()
    {
        var run = TwoPlayersPaired();
        var match = run.Pairings.Single(p => p.HasPlayer);

        run.MatchResult(match.MatchId!, null, Connected, Rules, new Random(60), 4_000);

        Assert.Equal(ArenaRun.BothLost, match.Winner);
        int life = 100 - Rules.LifeLost(1);
        Assert.Equal([(0, 1, life), (0, 1, life)], new[] { run.Teams[0], run.Teams[1] }.Select(t => (t.Wins, t.Losses, t.Life)));
    }

    [Fact]
    // Game rule: the one report names the team whose games are gone as the winner: the team still connected takes the loss
    // (they lost), and the gone team is out.
    public void AReportForAGoneTeamStandsAndTheGoneTeamIsOut()
    {
        var run = TwoPlayersPaired();
        var match = run.Pairings.Single(p => p.HasPlayer);
        int sideOfTwo = match.A == 1 ? 0 : 1;

        run.MatchResult(match.MatchId!, sideOfTwo, new HashSet<string> { Me }, Rules, new Random(61), 4_000);

        Assert.Equal(1, match.Winner);
        Assert.Equal((1, 100 - Rules.LifeLost(1)), (run.Teams[0].Losses, run.Teams[0].Life));
        Assert.True(run.Teams[1].FinalRank > 0);
        Assert.True(run.Teams[0].In);
    }

    [Fact]
    // A node match that never ends (no launch, a node gone) is decided at the Match deadline as one with no report.
    public void ANodeMatchThatNeverEndsIsDecidedAtTheMatchDeadline()
    {
        var (run, _) = Start(new Random(62));
        run.SelectAbsent(Me, Roster, Rules, new Random(63), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(64), 3_000);
        Assert.Equal(3_000 + Rules.MatchDeadlineSeconds * 1000L, run.DeadlineMs);

        Assert.False(run.Tick(run.DeadlineMs - 1, Roster, Rules, Bots, new Random(65), Connected).Accepted);
        run.Tick(run.DeadlineMs, Roster, Rules, Bots, new Random(65), Connected);

        Assert.Equal((ArenaPhase.Results, 0, 1), (run.Phase, run.Pairings.Single(p => p.HasPlayer).Winner, run.Teams[0].Wins));
    }

    [Fact]
    // Played to its end: each team out gets a final rank, the last one in is 1st, every rank once.
    public void AnArenaPlayedOutRanksEveryTeamOnce()
    {
        var random = new Random(21);
        var (run, _) = Start(random);
        run.SelectAbsent(Me, Roster, Rules, random, 2_000);
        var effects = new List<ArenaEffect>();
        for (int round = 0; round < 100 && run.Phase != ArenaPhase.Ended; round++)
        {
            effects.AddRange(run.PlayersIn.Any()
                ? run.ShopAbsent(Me, run.Round, Rules, Bots, random, 3_000).Effects
                : run.Tick(run.DeadlineMs, Roster, Rules, Bots, random, Connected).Effects);
            foreach (var pairing in run.Pairings.Where(p => p.HasPlayer && p.Winner is null).ToList())
            {
                effects.AddRange(run.MatchResult(pairing.MatchId!, 1, Connected, Rules, random, 3_000).Effects);
            }

            if (run.Phase == ArenaPhase.Results)
            {
                effects.AddRange(run.Checkin(Me, run.Round, Rules, random, 3_000).Effects);
            }
        }

        Assert.Equal(ArenaPhase.Ended, run.Phase);
        Assert.Equal(Enumerable.Range(1, 8), run.Teams.Select(t => t.FinalRank).Order());
        Assert.Contains(new ArenaFinished(), effects);
    }

    [Fact]
    // Two players in different matches: the first one's game closes its shop and plays; its result, in before the other's
    // shop has closed, is kept, and the round ends with the other match's result once that shop has closed.
    public void AResultInBeforeTheLastShopClosesIsKept()
    {
        var (run, _) = ArenaRun.Start("arena1", [.. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<(string, string, bool)>)
            [.. Enumerable.Range(0, 2).Select(i => t < 2 && i == 0 ? (t == 0 ? Me : "two", "P", false) : ($"bot{t}{i}", "Bot", true))])],
            Roster, Rules, new Random(27), 1_000);
        var mine = run.Pairings.Single(p => p.A == 0 || p.B == 0);
        var theirs = run.Pairings.Single(p => p.A == 1 || p.B == 1);
        Assert.NotSame(mine, theirs);
        run.SelectAbsent(Me, Roster, Rules, new Random(28), 2_000);
        run.SelectAbsent("two", Roster, Rules, new Random(28), 2_000);
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(29), 3_000);

        Assert.True(run.MatchResult(mine.MatchId!, 0, Connected, Rules, new Random(30), 4_000).Accepted);
        Assert.Equal((ArenaPhase.Shop, 1, 1), (run.Phase, run.Round, run.Teams[0].Wins));
        Assert.False(run.MatchResult(mine.MatchId!, 0, Connected, Rules, new Random(30), 4_000).Accepted);
        Assert.Equal([new SendPhaseConfigs()], run.ShopAbsent("two", 1, Rules, Bots, new Random(31), 5_000).Effects);
        Assert.Equal(ArenaPhase.Match, run.Phase);
        run.Checkin(Me, 1, Rules, new Random(32), 5_500);
        run.MatchResult(theirs.MatchId!, 0, Connected, Rules, new Random(32), 6_000);

        // Waits for the other player's game to check in.
        Assert.Equal((ArenaPhase.Results, 1, 1, 1), (run.Phase, run.Round, run.Teams[0].Wins, run.Teams[1].Wins));
        run.Checkin("two", 1, Rules, new Random(33), 7_000);

        Assert.Equal((ArenaPhase.Shop, 2), (run.Phase, run.Round));
    }

    [Fact]
    // Seven teams left: the odd one out plays its mirror, which it keeps as its opponent; the rest are paired once each.
    public void AnOddTeamOutPlaysItsMirror()
    {
        var (run, _) = Start(new Random(23));
        run.SelectAbsent(Me, Roster, Rules, new Random(24), 2_000);
        // A bots' team at no health goes out when the round ends, whatever its match.
        run.Teams[5].Life = -1_000;
        run.ShopAbsent(Me, 1, Rules, Bots, new Random(25), 3_000);

        run.Checkin(Me, 1, Rules, new Random(26), 2_500);
        run.MatchResult(run.Pairings.Single(p => p.HasPlayer).MatchId!, 0, Connected, Rules, new Random(26), 3_000);

        Assert.Equal((2, 8), (run.Round, run.Teams[5].FinalRank));
        var mirror = Assert.Single(run.Pairings, p => p.B == ArenaRun.Mirror);
        Assert.Equal(ArenaRun.Mirror, run.Teams[mirror.A].Opponents.Last());
        Assert.Equal([0, 1, 2, 3, 4, 6, 7], run.Pairings.SelectMany(p => new[] { p.A, p.B }).Where(t => t != ArenaRun.Mirror).Order());
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
