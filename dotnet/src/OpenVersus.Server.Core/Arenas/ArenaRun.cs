using System.Text.Json.Serialization;

namespace OpenVersus.Server.Core.Arenas;

// An Arena from its start to its end: the server's state machine for the mode (docs/ARENAS.md; the bench probes of
// 2026-10-11 in the archive's arenas-protocol.md). PURE: no stores, no clock, no network. Each transition takes what it
// needs (the time, a Random, the rules, the fighter roster) and returns what must happen as data (ArenaEffect); the Arena
// worker keeps the run as one JSON document, applies a transition under compare-and-set, then carries out the effects.
//
// The run mirrors the game's own pre-match chain (UMvsPreMatchStateMachine): a config whose reader has fighters on offer
// opens the Arena character select, one without opens the shop, and the match loads once the shop closes.
//
//   CharacterSelect  every player is offered SelectableCount fighters; a reroll costs CharacterRerollCost and settles AT ONCE
//                    (the game reads its coins from the answer); a pick ends their part, Random picks from the offer and
//                    gains RandomCharacterBonus; an absent player (their game's timer, or the server's deadline) is given a
//                    random fighter from their offer WITHOUT the bonus (they did not choose Random). All picked: bots take a
//                    random fighter, and the shop opens.
//   Shop             every player still in is offered ShopPages pages of OffersPerPage items (their game's local rerolls walk
//                    the pages); purchases, sales and rerolls settle when the shop closes, from the game's summary (buying
//                    nothing is a valid strategy: saving earns interest), replayed as the game's local shop made them: the
//                    inventory is MaxItemsInInventory slots (an empty one: Slug ""), a purchase levels a slot holding the
//                    same item or fills the first empty slot (none: no purchase), a sale empties its slot in place and pays
//                    that slot's SellValue (the sum of what each copy added). All closed (or the deadline): every player is
//                    sent their config again, ready to start (bReadyToStart: the game goes from its shop to the match only
//                    then), and the round's matches are played.
//   Match            the shops have closed: each pairing with a player in it plays its node match (launched at the
//                    round's start); a pairing of bots only is decided now by IArenaBotMatches (random for now: an
//                    interface so a smarter rule can replace it). The loser loses LifeLost(round). All decided: the round
//                    ends. A node's result that comes in while another player's shop is still open is kept.
//   (round end)      a team at 0 health or below is out (its final rank: the teams left + 1, worse health ranked lower);
//                    one team left (or none) ends the Arena; otherwise every player still in gains the round's coins,
//                    interest on what they kept, a win's coins and their streak's, the next round is paired and launched,
//                    and its shop opens (no character select after the first round).
//
// Pairing: at the round's start, every team still in with another at random; an odd team out plays its own MIRROR (a bot
// team copying it, Mirror = -1). Each team's Opponents keeps its opponent per round: the game's Arena Status reads the last
// one before the match (crash 2026-10-11 when it was empty). A pairing with a player is given its match id then (the
// pre-match configs name it; the launch reuses it), and its team A is one with a player in it: the launch puts A on the
// match's side 0, so the side a node reports as the winner is a team again (TeamOnSide). Its node match is launched then
// too (LaunchPairing): the game's pre-match screens (character select, shop) run on that match's own config, as a normal
// match's perks screen does, and the game loads the match by itself once its shop closes (seen 2026-10-11).
//
// UNPROBED (shapes taken from the binary, not yet seen on the wire): what the Random button sends with a full offer (the
// edge passes chooseRandom), ShopPhaseDetails' ItemTransactions entries (purchase: LocalShopIndex = page, ItemIndex = offer;
// sale: ItemIndex = inventory slot), round 2's messages (arena_checkin, OnArenaNextMatch), a match that never reports.

/// <summary>Where an Arena run is.</summary>
public enum ArenaPhase
{
    CharacterSelect,
    Shop,
    Match,
    Ended,
}

/// <summary>A participant: a player or a bot.</summary>
public sealed class ArenaRunPlayer
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public bool IsBot { get; set; }
    public int Team { get; set; }

    /// <summary>The fighters on offer in the character select (empty once picked).</summary>
    public List<string> Pool { get; set; } = [];

    public int Coins { get; set; }
    public string? Character { get; set; }
    public string? Skin { get; set; }

    /// <summary>They chose Random (the bonus was paid).</summary>
    public bool RandomPick { get; set; }

    /// <summary>Their shop has closed this round.</summary>
    public bool ShopDone { get; set; }

    public List<ArenaOwnedItem> Inventory { get; set; } = [];

    /// <summary>This round's shop pages: item slugs.</summary>
    public List<List<string>> ShopPages { get; set; } = [];
}

/// <summary>An inventory slot: an item a player owns, or empty (Slug "").</summary>
public sealed class ArenaOwnedItem
{
    public string Slug { get; set; } = "";
    public int Level { get; set; }
    public int Xp { get; set; }

    /// <summary>What selling it pays: what each copy bought added (half its cost).</summary>
    public int SellValue { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Slug.Length == 0;
}

/// <summary>A team of two.</summary>
public sealed class ArenaRunTeam
{
    public int Index { get; set; }
    public List<string> Members { get; set; } = [];
    public int Life { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int WinStreak { get; set; }
    public int LoseStreak { get; set; }

    /// <summary>Its opponent in each round (<see cref="ArenaRun.Mirror"/>: its own mirror).</summary>
    public List<int> Opponents { get; set; } = [];

    /// <summary>Its final rank once out (1 = the winner); 0 while it is still in.</summary>
    public int FinalRank { get; set; }

    [JsonIgnore]
    public bool In => FinalRank == 0;
}

/// <summary>One of a round's 2v2s.</summary>
public sealed class ArenaRunPairing
{
    /// <summary>A team; one with a player in it when the pairing has one. The match's side 0.</summary>
    public int A { get; set; }

    /// <summary>The other team, or <see cref="ArenaRun.Mirror"/>.</summary>
    public int B { get; set; }

    /// <summary>A player is in it: it is played on a node; otherwise decided by IArenaBotMatches.</summary>
    public bool HasPlayer { get; set; }

    /// <summary>Its node match's id, given when it is paired (a pairing with a player only).</summary>
    public string? MatchId { get; set; }

    /// <summary>The winning team (A, B or Mirror), once decided.</summary>
    public int? Winner { get; set; }

    /// <summary>The team on the match's side <paramref name="side"/> (0: A, 1: B).</summary>
    public int TeamOnSide(int side) => side == 0 ? A : B;
}

/// <summary>What a transition asks the worker to do.</summary>
public abstract record ArenaEffect;

/// <summary>Send every player still in their config for the current phase (character select, shop).</summary>
public sealed record SendPhaseConfigs : ArenaEffect;

/// <summary>Launch the pairing's match on a node, under its MatchId (at the round's start).</summary>
public sealed record LaunchPairing(int Pairing) : ArenaEffect;

/// <summary>The Arena has ended.</summary>
public sealed record ArenaFinished : ArenaEffect;

/// <summary>A transition's outcome: whether the call was taken, and the effects.</summary>
public sealed record ArenaStep(bool Accepted, IReadOnlyList<ArenaEffect> Effects)
{
    public static ArenaStep Refused { get; } = new(false, []);
}

/// <summary>An item transaction from the shop's closing summary (ShopPhaseDetails.ItemTransactions).</summary>
public sealed record ArenaTransaction(bool Purchase, int Page, int Index);

/// <summary>Decides a round's match between bots (no node). An interface so the rule can change without its callers.</summary>
public interface IArenaBotMatches
{
    /// <summary>The winner of <paramref name="pairing"/>: its A, its B (a team or <see cref="ArenaRun.Mirror"/>).</summary>
    int Winner(ArenaRun run, ArenaRunPairing pairing, Random random);
}

/// <summary>For now: a coin toss.</summary>
public sealed class RandomArenaBotMatches : IArenaBotMatches
{
    public int Winner(ArenaRun run, ArenaRunPairing pairing, Random random) => random.Next(2) == 0 ? pairing.A : pairing.B;
}

/// <summary>An Arena run (see the header of ArenaRun.cs).</summary>
public sealed class ArenaRun
{
    /// <summary>The opponent of a team playing its own mirror.</summary>
    public const int Mirror = -1;

    public string Id { get; set; } = "";
    public int Round { get; set; }
    public ArenaPhase Phase { get; set; }

    /// <summary>When the current phase is decided for whoever has not acted (0: none).</summary>
    public long DeadlineMs { get; set; }

    public List<ArenaRunTeam> Teams { get; set; } = [];
    public Dictionary<string, ArenaRunPlayer> Players { get; set; } = [];

    /// <summary>The current round's pairings.</summary>
    public List<ArenaRunPairing> Pairings { get; set; } = [];

    /// <summary>The players (not bots) still in.</summary>
    [JsonIgnore]
    public IEnumerable<ArenaRunPlayer> PlayersIn => Players.Values.Where(p => !p.IsBot && Teams[p.Team].In);

    /// <summary>
    /// A new run: <paramref name="teams"/> by index (every place filled, bots included), each player offered fighters from
    /// <paramref name="roster"/>, round 1 paired, the character select open until its deadline.
    /// </summary>
    public static (ArenaRun Run, ArenaStep Step) Start(string id, IReadOnlyList<IReadOnlyList<(string Id, string Username, bool IsBot)>> teams,
        IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules, Random random, long nowMs)
    {
        var run = new ArenaRun { Id = id, Round = 1, Phase = ArenaPhase.CharacterSelect };
        for (int t = 0; t < teams.Count; t++)
        {
            run.Teams.Add(new ArenaRunTeam { Index = t, Members = [.. teams[t].Select(p => p.Id)], Life = rules.StartingLife });
            foreach (var (pid, username, isBot) in teams[t])
            {
                run.Players[pid] = new ArenaRunPlayer
                {
                    Id = pid, Username = username, IsBot = isBot, Team = t, Coins = isBot ? 0 : rules.StartingCoins,
                    Pool = isBot ? [] : Offer(roster, rules.SelectableCount, random),
                    Inventory = [.. Enumerable.Range(0, rules.MaxItemsInInventory).Select(_ => new ArenaOwnedItem())],
                };
            }
        }

        run.Pair(random, nowMs);
        run.DeadlineMs = nowMs + (rules.CharacterSelectSeconds + rules.GraceSeconds) * 1000L;
        return (run, new ArenaStep(true, [.. run.Launches(), new SendPhaseConfigs()]));
    }

    /// <summary>A character reroll: a new offer for <see cref="ArenaRules.CharacterRerollCost"/>, settled now.</summary>
    public ArenaStep RerollCharacters(string playerId, IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules, Random random)
    {
        if (Phase != ArenaPhase.CharacterSelect || !Players.TryGetValue(playerId, out var player) || player.IsBot || player.Character is not null
            || player.Coins < rules.CharacterRerollCost)
        {
            return ArenaStep.Refused;
        }

        player.Coins -= rules.CharacterRerollCost;
        player.Pool = Offer(roster, rules.SelectableCount, random);
        return new ArenaStep(true, []);
    }

    /// <summary>
    /// A pick: <paramref name="character"/> from their offer (in <paramref name="skin"/>, else its first), or Random
    /// (<paramref name="chooseRandom"/>: one of the offer, with the bonus). The last pick opens the shop.
    /// </summary>
    public ArenaStep Select(string playerId, string? character, string? skin, bool chooseRandom, IReadOnlyList<(string Character, string[] Skins)> roster,
        ArenaRules rules, Random random, long nowMs)
    {
        if (Phase != ArenaPhase.CharacterSelect || !Players.TryGetValue(playerId, out var player) || player.IsBot || player.Character is not null)
        {
            return ArenaStep.Refused;
        }

        if (chooseRandom)
        {
            Pick(player, player.Pool[random.Next(player.Pool.Count)], null, roster);
            player.Coins += rules.RandomCharacterBonus;
            player.RandomPick = true;
        }
        else if (character is not null && player.Pool.Contains(character))
        {
            Pick(player, character, skin, roster);
        }
        else
        {
            return ArenaStep.Refused;
        }

        return new ArenaStep(true, AllPicked() ? EndCharacterSelect(roster, rules, random, nowMs) : []);
    }

    /// <summary>A player's game says they did not pick in time: a random fighter from their offer, no bonus.</summary>
    public ArenaStep SelectAbsent(string playerId, IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules, Random random, long nowMs)
    {
        if (Phase != ArenaPhase.CharacterSelect || !Players.TryGetValue(playerId, out var player) || player.IsBot || player.Character is not null)
        {
            return ArenaStep.Refused;
        }

        Pick(player, player.Pool[random.Next(player.Pool.Count)], null, roster);
        return new ArenaStep(true, AllPicked() ? EndCharacterSelect(roster, rules, random, nowMs) : []);
    }

    /// <summary>A player's shop closed: their rerolls and transactions settle; the last one closed starts the round's matches.</summary>
    public ArenaStep ShopClosed(string playerId, int round, int rerolls, IReadOnlyList<ArenaTransaction> transactions, ArenaRules rules,
        IArenaBotMatches botMatches, Random random, long nowMs)
    {
        if (Phase != ArenaPhase.Shop || round != Round || !Players.TryGetValue(playerId, out var player) || player.IsBot || player.ShopDone)
        {
            return ArenaStep.Refused;
        }

        player.Coins = Math.Max(0, player.Coins - Math.Max(0, rerolls) * rules.ShopRerollCost);
        foreach (var t in transactions)
        {
            if (t.Purchase)
            {
                Buy(player, t.Page, t.Index, rules);
            }
            else
            {
                Sell(player, t.Index);
            }
        }

        player.ShopDone = true;
        return new ArenaStep(true, PlayersIn.All(p => p.ShopDone) ? EndShop(rules, botMatches, random, nowMs) : []);
    }

    /// <summary>A player's game says their shop closed with nothing done (their timer): they bought nothing.</summary>
    public ArenaStep ShopAbsent(string playerId, int round, ArenaRules rules, IArenaBotMatches botMatches, Random random, long nowMs)
    {
        if (Phase != ArenaPhase.Shop || round != Round || !Players.TryGetValue(playerId, out var player) || player.IsBot || player.ShopDone)
        {
            return ArenaStep.Refused;
        }

        player.ShopDone = true;
        return new ArenaStep(true, PlayersIn.All(p => p.ShopDone) ? EndShop(rules, botMatches, random, nowMs) : []);
    }

    /// <summary>
    /// A node match's result: <paramref name="winnerTeam"/> won. Kept while another player's shop is still open (their
    /// game closed its shop and played); the last result once every shop has closed ends the round.
    /// </summary>
    public ArenaStep MatchResult(string matchId, int winnerTeam, IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules,
        Random random, long nowMs)
    {
        if (Phase is not (ArenaPhase.Shop or ArenaPhase.Match) || Pairings.FirstOrDefault(p => p.MatchId == matchId) is not { Winner: null } pairing
            || (winnerTeam != pairing.A && winnerTeam != pairing.B))
        {
            return ArenaStep.Refused;
        }

        Decide(pairing, winnerTeam, rules);
        return new ArenaStep(true, Phase == ArenaPhase.Match && Pairings.All(p => p.Winner is not null) ? EndRound(rules, random, nowMs) : []);
    }

    /// <summary>The deadline passed: whoever has not acted is decided for (absent pick; shop closed with nothing bought).</summary>
    public ArenaStep Tick(long nowMs, IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules, IArenaBotMatches botMatches, Random random)
    {
        if (DeadlineMs == 0 || nowMs < DeadlineMs)
        {
            return ArenaStep.Refused;
        }

        switch (Phase)
        {
            case ArenaPhase.CharacterSelect:
                foreach (var player in PlayersIn.Where(p => p.Character is null))
                {
                    Pick(player, player.Pool[random.Next(player.Pool.Count)], null, roster);
                }

                return new ArenaStep(true, EndCharacterSelect(roster, rules, random, nowMs));
            case ArenaPhase.Shop:
                foreach (var player in PlayersIn)
                {
                    player.ShopDone = true;
                }

                return new ArenaStep(true, EndShop(rules, botMatches, random, nowMs));
            default:
                return ArenaStep.Refused;
        }
    }

    private bool AllPicked() => PlayersIn.All(p => p.Character is not null);

    // Bots take a random fighter; every player still in gets this round's shop pages.
    private List<ArenaEffect> EndCharacterSelect(IReadOnlyList<(string Character, string[] Skins)> roster, ArenaRules rules, Random random, long nowMs)
    {
        foreach (var bot in Players.Values.Where(p => p.IsBot && p.Character is null))
        {
            var fighter = roster[random.Next(roster.Count)];
            bot.Character = fighter.Character;
            bot.Skin = fighter.Skins[random.Next(fighter.Skins.Length)];
        }

        return OpenShop(rules, random, nowMs);
    }

    private List<ArenaEffect> OpenShop(ArenaRules rules, Random random, long nowMs)
    {
        Phase = ArenaPhase.Shop;
        if (!PlayersIn.Any())
        {
            // No player left in: the bots' Arena is decided to its end at once.
            DeadlineMs = 0;
            return EndShop(rules, new RandomArenaBotMatches(), random, nowMs);
        }

        foreach (var player in PlayersIn)
        {
            player.ShopDone = false;
            player.ShopPages = ArenaItems.Pages(Round, rules.ShopPages, rules.OffersPerPage, random);
        }

        DeadlineMs = nowMs + (rules.ShopSeconds + rules.GraceSeconds) * 1000L;
        return [new SendPhaseConfigs()];
    }

    // The round's matches: the bots' pairings decided now; the players' are played on the nodes launched at the round's start.
    private List<ArenaEffect> EndShop(ArenaRules rules, IArenaBotMatches botMatches, Random random, long nowMs)
    {
        Phase = ArenaPhase.Match;
        DeadlineMs = 0;
        foreach (var pairing in Pairings.Where(p => !p.HasPlayer))
        {
            Decide(pairing, botMatches.Winner(this, pairing, random), rules);
        }

        // Every match decided (no player in this round, or their results came in during the shop): the round ends now.
        // Otherwise every player is sent their config ready to start.
        return Pairings.All(p => p.Winner is not null) ? EndRound(rules, random, nowMs) : [new SendPhaseConfigs()];
    }

    // The node launches for the current round: its pairings with a player.
    private List<ArenaEffect> Launches() => [.. Pairings.Select((p, i) => (p, i)).Where(x => x.p.HasPlayer).Select(x => (ArenaEffect)new LaunchPairing(x.i))];

    private void Decide(ArenaRunPairing pairing, int winner, ArenaRules rules)
    {
        pairing.Winner = winner;
        int loser = winner == pairing.A ? pairing.B : pairing.A;
        if (winner != Mirror)
        {
            var won = Teams[winner];
            won.Wins++;
            won.WinStreak++;
            won.LoseStreak = 0;
        }

        if (loser != Mirror)
        {
            var lost = Teams[loser];
            lost.Losses++;
            lost.LoseStreak++;
            lost.WinStreak = 0;
            lost.Life -= rules.LifeLost(Round);
        }
    }

    // Teams at 0 health go out; one left ends the Arena; otherwise coins, the next round's pairings and its shop.
    private List<ArenaEffect> EndRound(ArenaRules rules, Random random, long nowMs)
    {
        var outNow = Teams.Where(t => t.In && t.Life <= 0).OrderByDescending(t => t.Life).ToList();
        int left = Teams.Count(t => t.In) - outNow.Count;
        for (int i = 0; i < outNow.Count; i++)
        {
            outNow[i].FinalRank = left + 1 + i;
        }

        var stillIn = Teams.Where(t => t.In).ToList();
        if (stillIn.Count <= 1)
        {
            if (stillIn.Count == 1)
            {
                stillIn[0].FinalRank = 1;
            }

            Phase = ArenaPhase.Ended;
            DeadlineMs = 0;
            return [new ArenaFinished()];
        }

        foreach (var player in PlayersIn)
        {
            var team = Teams[player.Team];
            bool won = Pairings.Any(p => p.Winner == player.Team);
            player.Coins += rules.Interest(player.Coins) + At(rules.CurrencyPerRound, Round - 1) + (won ? rules.CurrencyForWin : 0)
                + At(rules.CurrencyForWinStreak, team.WinStreak) + At(rules.CurrencyForLoseStreak, team.LoseStreak);
        }

        Round++;
        Pair(random, nowMs);
        var effects = Launches();
        effects.AddRange(OpenShop(rules, random, nowMs));
        return effects;
    }

    // Every team still in against another at random; an odd one out against its mirror. A pairing with a player: its id,
    // and a team with a player as A.
    private void Pair(Random random, long nowMs)
    {
        int[] order = [.. Teams.Where(t => t.In).Select(t => t.Index)];
        random.Shuffle(order);
        Pairings = [];
        for (int i = 0; i < order.Length; i += 2)
        {
            int a = order[i], b = i + 1 < order.Length ? order[i + 1] : Mirror;
            if (!HasPlayer(a) && HasPlayer(b))
            {
                (a, b) = (b, a);
            }

            bool hasPlayer = HasPlayer(a);
            Pairings.Add(new ArenaRunPairing { A = a, B = b, HasPlayer = hasPlayer, MatchId = hasPlayer ? NewMatchId(nowMs, random) : null });
            Teams[a].Opponents.Add(b);
            if (b != Mirror)
            {
                Teams[b].Opponents.Add(a);
            }
        }
    }

    private bool HasPlayer(int team) => team != Mirror && Teams[team].Members.Any(m => Players.TryGetValue(m, out var p) && !p.IsBot);

    private void Buy(ArenaRunPlayer player, int page, int index, ArenaRules rules)
    {
        if (page < 0 || page >= player.ShopPages.Count || index < 0 || index >= player.ShopPages[page].Count
            || ArenaItems.Of(player.ShopPages[page][index]) is not { } item || player.Coins < item.Cost)
        {
            return;
        }

        if (player.Inventory.FirstOrDefault(i => i.Slug == item.Slug) is { } owned)
        {
            // A duplicate is experience toward its next level (ItemXpPerLevel, by level); at the top it is not bought.
            if (owned.Level >= rules.ItemXpPerLevel.Count)
            {
                return;
            }

            player.Coins -= item.Cost;
            owned.Xp++;
            owned.SellValue += item.Cost / 2;
            if (owned.Xp >= rules.ItemXpPerLevel[owned.Level])
            {
                owned.Xp = 0;
                owned.Level++;
            }

            return;
        }

        if (player.Inventory.FirstOrDefault(i => i.IsEmpty) is not { } free)
        {
            return;
        }

        player.Coins -= item.Cost;
        (free.Slug, free.Level, free.Xp, free.SellValue) = (item.Slug, 1, 0, item.Cost / 2);
    }

    private static void Sell(ArenaRunPlayer player, int slot)
    {
        if (slot < 0 || slot >= player.Inventory.Count || player.Inventory[slot] is not { IsEmpty: false } sold)
        {
            return;
        }

        player.Coins += sold.SellValue;
        player.Inventory[slot] = new ArenaOwnedItem();
    }

    private static void Pick(ArenaRunPlayer player, string character, string? skin, IReadOnlyList<(string Character, string[] Skins)> roster)
    {
        string[] skins = roster.FirstOrDefault(f => f.Character == character).Skins ?? [];
        player.Character = character;
        player.Skin = !string.IsNullOrEmpty(skin) && skins.Contains(skin) ? skin : skins.FirstOrDefault() ?? "";
        player.Pool = [];
    }

    private static List<string> Offer(IReadOnlyList<(string Character, string[] Skins)> roster, int count, Random random) =>
        [.. roster.Select(f => f.Character).OrderBy(_ => random.Next()).Take(count)];

    private static int At(IReadOnlyList<int> table, int index) => table.Count == 0 ? 0 : table[Math.Clamp(index, 0, table.Count - 1)];

    // An ObjectId's shape, as every match id has: its seconds, then 8 random bytes (an ObjectId's random and counter parts).
    private static string NewMatchId(long nowMs, Random random) => $"{(uint)(nowMs / 1000):x8}{random.GetHexString(16, lowercase: true)}";
}
