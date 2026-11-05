using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.FunFacts;

// The fun fact the OpenVersus client shows a player after login (an admin_banner notification, sent by
// create_party_lobby when /access armed fun_fact_pending), ported from the TS server's services/funFactsService.ts: every
// generator that has something to say about the player's stats (Mongo playerstats {account_id}, eloratings
// {account_id}) adds a fact to the pool, and one is picked at random. The generators keep the TS arithmetic and wording,
// its quirks included (the most-played count drops a character's 1v1 games when its 2v2 entry has no wins or losses;
// signature moves count only the 2v2 stats of a character played in both). Checked against the TS server on real
// players: tools/funfacts/funfacts_pools.mjs and FunFactsTests.FunFactsMatchTheTsServer.

public sealed record FunFact(string Title, string Message);

public interface IFunFacts
{
    /// <summary>A fact about the player picked at random, or null when their stats have nothing to say (or there are none).</summary>
    Task<FunFact?> RandomAsync(string accountId, CancellationToken ct = default);
}

internal sealed class FunFactsService(IServiceProvider services, ILogger<FunFactsService> log) : IFunFacts
{
    public async Task<FunFact?> RandomAsync(string accountId, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return null;
        }

        try
        {
            var filter = new BsonDocument("account_id", accountId);
            var stats = await mongo.GetCollection<BsonDocument>("playerstats").Find(filter).FirstOrDefaultAsync(ct);
            if (stats is null)
            {
                return null;
            }

            var elo = await mongo.GetCollection<BsonDocument>("eloratings").Find(filter).FirstOrDefaultAsync(ct);
            var pool = FunFactRules.Pool(FunFactRules.Json(stats), elo is null ? null : FunFactRules.Json(elo));
            return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError("Error generating fact for {Player}: {Error}", accountId, e.Message);
            return null;
        }
    }
}

public static partial class FunFactRules
{
    private sealed record Ctx(JsonObject Stats, JsonObject Elo, JsonObject Agg, JsonObject Chars1v1, JsonObject Chars2v2, JsonArray Recent1v1, JsonArray Recent2v2);

    private static readonly IReadOnlyDictionary<string, string> s_names = Load();

    /// <summary>Every fact the stats give, in the generators' order (getRandomFunFact's pool).</summary>
    public static List<FunFact> Pool(JsonObject stats, JsonObject? elo)
    {
        var c = new Ctx(stats, elo ?? [], Obj(stats["aggregate"]), Obj(stats["characters_1v1"]), Obj(stats["characters_2v2"]),
            Arr(stats["recent_matches_1v1"]), Arr(stats["recent_matches_2v2"]));
        var pool = new List<FunFact>();
        foreach (var generator in s_generators)
        {
            try
            {
                if (generator(c) is { } fact)
                {
                    pool.Add(fact);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
            {
                // As there: a generator that fails is skipped.
            }
        }

        return pool;
    }

    /// <summary>
    /// A stored document as the generators read it: numbers as numbers, ids and dates as text, and its keys in the order
    /// a JavaScript object has them (integer-like keys first), as the TS server's lean read does.
    /// </summary>
    public static JsonObject Json(BsonDocument doc) => (JsonObject)Js.Parse(Js.Stringify(ToJson(doc)))!;

    private static JsonNode? ToJson(BsonValue value) => value switch
    {
        BsonDocument d => new JsonObject(d.Elements.Select(e => KeyValuePair.Create(e.Name, ToJson(e.Value)))),
        BsonArray a => new JsonArray(a.Select(ToJson).ToArray()),
        BsonInt32 i => JsonValue.Create(i.Value),
        BsonInt64 l => JsonValue.Create(l.Value),
        BsonDouble n => JsonValue.Create(n.Value),
        BsonDecimal128 m => JsonValue.Create((double)m.Value),
        BsonBoolean b => JsonValue.Create(b.Value),
        BsonString s => JsonValue.Create(s.Value),
        BsonNull or BsonUndefined => null,
        BsonDateTime t => JsonValue.Create(t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    private static readonly Func<Ctx, FunFact?>[] s_generators =
    [
        // === Accuracy / hit rate ===
        c =>
        {
            double used = Or0(c.Agg["totalAttacksUsed"]), hit = Or0(c.Agg["totalAttacksHit"]);
            if (used < 200)
            {
                return null;
            }
            double pct = Round(hit / used * 100);
            if (pct < 25)
            {
                return F("Whiff lord", $"Only {S(pct)}% of your swings connect. Stormtroopers are calling for advice.");
            }
            if (pct < 40)
            {
                return F("Hit rate", $"You land {S(pct)}% of your attacks. The other {S(100 - pct)}% are prayers.");
            }
            if (pct < 55)
            {
                return F("Hit rate", $"{S(pct)}% of your swings land. Respectable. Ish.");
            }
            if (pct < 70)
            {
                return F("Surgical", $"{S(pct)}% accuracy. Somebody's been practicing.");
            }
            return F("Aimbot?", $"{S(pct)}% of your attacks hit. We're filing a report.");
        },
        c =>
        {
            double used = Or0(c.Agg["totalSpecialsUsed"]), hit = Or0(c.Agg["totalSpecialsHit"]);
            if (used < 50)
            {
                return null;
            }
            double pct = Round(hit / used * 100);
            if (pct < 30)
            {
                return F("Special delivery", $"Only {S(pct)}% of your specials connect. They're called specials, not scatters.");
            }
            if (pct > 70)
            {
                return F("Special delivery", $"{S(pct)}% of your specials land. Clean.");
            }
            return null;
        },
        // === Ringout K/D ===
        c =>
        {
            double r = Or0(c.Agg["totalRingouts"]), rr = Or0(c.Agg["totalRingoutsReceived"]);
            if (r + rr < 10)
            {
                return null;
            }
            double ratio = r / Math.Max(rr, 1);
            string msg = ratio > 2 ? $"You ring out {Fixed(ratio, 2)}× more than you get sent. Bully."
                : ratio < 0.5 ? $"You get rung out {Fixed(1 / ratio, 2)}× more than you ring out. Ouch."
                : $"Your ringout ratio is {Fixed(ratio, 2)} ({S(r)}/{S(rr)}).";
            return F("Splat math", msg);
        },
        // === Parries ===
        c =>
        {
            double count = new[] { "totalParries", "totalAttacksParried", "totalParriesLanded", "totalSuccessfulParries" }
                .Select(k => Or0(c.Agg[k])).FirstOrDefault(v => v != 0);
            if (count < 1)
            {
                return null;
            }
            if (count >= 100)
            {
                return F("Timing god", $"{PrettyNumber(count)} parries. Your reactions are witchcraft.");
            }
            return F("Timing is everything", $"You've parried {PrettyNumber(count)} attack{Plural(count)}.");
        },
        // === Defense vs offense ===
        c =>
        {
            double hits = Or0(c.Agg["totalHitsTaken"]), dodges = Or0(c.Agg["totalDodgesUsed"]);
            if (hits + dodges < 200)
            {
                return null;
            }
            double pct = Round(dodges / (hits + dodges) * 100);
            if (pct >= 60)
            {
                return F("Untouchable", $"For every 10 times someone attacks you, you dodge {S(Round(pct / 10))}. Spiderman sense.");
            }
            if (pct <= 15)
            {
                return F("Wall", $"You only dodge {S(pct)}% of the time — you just eat the hits. Respect.");
            }
            return null;
        },
        c =>
        {
            double dd = Or0(c.Agg["totalDamageDodged"]);
            if (dd < 500)
            {
                return null;
            }
            return F("Matrix mode", $"You've dodged {PrettyNumber(dd)} damage. Money saved.");
        },
        // === Playstyle ===
        c =>
        {
            double air = Or0(c.Agg["totalAirDamageDealt"]), ground = Or0(c.Agg["totalGroundDamageDealt"]);
            if (air + ground < 1000)
            {
                return null;
            }
            double pct = Round(air / (air + ground) * 100);
            if (pct >= 70)
            {
                return F("Airborne menace", $"{S(pct)}% of your damage happens off the ground. Bird mode.");
            }
            if (pct <= 30)
            {
                return F("Feet planted", $"{S(100 - pct)}% of your damage is grounded. Unwavering.");
            }
            return null;
        },
        c =>
        {
            double normals = Or0(c.Agg["totalNormalAttacksUsed"]), specials = Or0(c.Agg["totalSpecialsUsed"]);
            if (normals + specials < 200)
            {
                return null;
            }
            double pct = Round(specials / (normals + specials) * 100);
            if (pct >= 50)
            {
                return F("Special order", $"Over half ({S(pct)}%) of your attacks are specials. Tier list manipulation.");
            }
            if (pct <= 15)
            {
                return F("Fundamentals only", $"Only {S(pct)}% of your attacks are specials. Pure neutral merchant.");
            }
            return null;
        },
        c =>
        {
            double ca = Or0(c.Agg["totalChargeAttacksUsed"]), total = Or0(c.Agg["totalAttacksUsed"]);
            if (total < 200 || ca < 50)
            {
                return null;
            }
            double pct = Round(ca / total * 100);
            return pct >= 20 ? F("Charger", $"{S(pct)}% of your attacks are charged. Patience is a weapon.") : null;
        },
        c =>
        {
            double fc = Or0(c.Agg["totalFullyChargedAttacksHit"]);
            if (fc < 5)
            {
                return null;
            }
            return F("Smash!", $"You've landed {PrettyNumber(fc)} FULLY charged attacks. Each one's a crime scene.");
        },
        // === Clutch / style ===
        c =>
        {
            double stolen = Or0(c.Agg["totalRingoutsWithLessDamage"]);
            if (stolen < 3)
            {
                return null;
            }
            return F("Clutch gene", $"You've stolen {PrettyNumber(stolen)} ringouts while behind on damage. Scammer.");
        },
        c =>
        {
            double low = Or0(c.Agg["totalRingoutsEnemyLowPercent"]);
            if (low < 3)
            {
                return null;
            }
            return F("Early exit", $"You've sent {PrettyNumber(low)} opponents out at low percent. Disrespectful.");
        },
        c =>
        {
            double proj = Or0(c.Agg["totalProjectileRingouts"]), total = Or0(c.Agg["totalRingouts"]);
            if (total < 10 || proj < 3)
            {
                return null;
            }
            double pct = Round(proj / total * 100);
            return pct >= 30 ? F("Camper alert", $"{S(pct)}% of your kills come from projectiles. From a safe distance.") : null;
        },
        // === Time-based ===
        c => N(c.Agg["totalCrouchTime"]) > 120
            ? F("Crouched up", $"You've crouched for {Duration(N(c.Agg["totalCrouchTime"]))}. Turtle activity detected.") : null,
        c => N(c.Agg["totalWallHangTime"]) > 30
            ? F("Spiderman mode", $"You've clung to walls for {Duration(N(c.Agg["totalWallHangTime"]))}. The stage isn't your friend.") : null,
        c =>
        {
            double air = Or0(c.Agg["totalAirTime"]), walk = Or0(c.Agg["totalWalkTime"]);
            if (air + walk < 300)
            {
                return null;
            }
            return air > walk * 1.5 ? F("Gravity? Never heard of her", $"You spend more time in the air than walking. {Duration(air)} airborne vs {Duration(walk)} walking.") : null;
        },
        c => N(c.Agg["totalPlatformDropThroughs"]) > 200
            ? F("Dropthrough addict", $"You've dropped through platforms {PrettyNumber(N(c.Agg["totalPlatformDropThroughs"]))} times. The platforms are tired of you.") : null,
        // === Ringout geography ===
        c =>
        {
            double left = Or0(c.Agg["totalLeftRingouts"]), right = Or0(c.Agg["totalRightRingouts"]), down = Or0(c.Agg["totalDownRingouts"]), up = Or0(c.Agg["totalUpRingouts"]);
            double total = left + right + down + up;
            if (total < 20)
            {
                return null;
            }
            double max = Math.Max(Math.Max(left, right), Math.Max(down, up));
            if (max / total < 0.45)
            {
                return null;
            }
            string side = max == left ? "left" : max == right ? "right" : max == down ? "bottom (spike city)" : "top (upward slams)";
            return F("Favorite exit", $"{S(Round(max / total * 100))}% of your ringouts go off the {side}. You have a side.");
        },
        c =>
        {
            double down = Or0(c.Agg["totalDownRingouts"]), total = Or0(c.Agg["totalRingouts"]);
            if (total < 20 || down < 5)
            {
                return null;
            }
            double pct = Round(down / total * 100);
            return pct >= 25 ? F("Spike specialist", $"{S(pct)}% of your kills are spikes. Brutal.") : null;
        },
        // === Character-based ===
        c =>
        {
            // { ...chars1v1 }, then each 2v2 entry added in: its wins and losses summed into a 1v1 one as
            // (a || 0) + b || 0, so a 2v2 entry without them zeroes the sum.
            var all = new List<(string Char, double Wins, double Losses)>();
            foreach (var (k, v) in c.Chars1v1)
            {
                all.Add((k, N(v?["wins"]), N(v?["losses"])));
            }

            foreach (var (k, v) in c.Chars2v2)
            {
                int at = all.FindIndex(e => e.Char == k);
                if (at >= 0 && c.Chars1v1[k] is not null)
                {
                    var e = all[at];
                    all[at] = (k, OrZero(OrZeroN(e.Wins) + N(v?["wins"])), OrZero(OrZeroN(e.Losses) + N(v?["losses"])));
                }
                else if (at >= 0)
                {
                    all[at] = (k, N(v?["wins"]), N(v?["losses"]));
                }
                else
                {
                    all.Add((k, N(v?["wins"]), N(v?["losses"])));
                }
            }

            var entries = all.Select(e => (e.Char, Games: OrZeroN(e.Wins) + OrZeroN(e.Losses))).Where(e => e.Games >= 3).OrderByDescending(e => e.Games).ToList();
            if (entries.Count == 0)
            {
                return null;
            }
            return F("Main character", $"{PrettyChar(entries[0].Char)} is your most-played fighter ({S(entries[0].Games)} games).");
        },
        c =>
        {
            var entries = c.Chars1v1.Select(e => (Char: e.Key, Wins: Or0(e.Value?["wins"]))).Where(e => e.Wins >= 3).OrderByDescending(e => e.Wins).ToList();
            if (entries.Count == 0)
            {
                return null;
            }
            return F("Workhorse", $"{PrettyChar(entries[0].Char)} has carried you to {S(entries[0].Wins)} wins in 1v1.");
        },
        c =>
        {
            var entries = c.Chars1v1.Select(e => (Char: e.Key, Streak: Or0(e.Value?["streak"]))).Where(e => e.Streak >= 3).OrderByDescending(e => e.Streak).ToList();
            if (entries.Count == 0)
            {
                return null;
            }
            return F("Hot streak", $"You're on a {S(entries[0].Streak)}-win streak with {PrettyChar(entries[0].Char)}. Keep it rolling.");
        },
        c =>
        {
            int streak = 0;
            for (int i = c.Recent1v1.Count - 1; i >= 0; i--)
            {
                if (Text(c.Recent1v1[i]?["result"]) == "loss")
                {
                    streak++;
                }
                else
                {
                    break;
                }
            }

            return streak < 3 ? null : F("Rough stretch", $"You've dropped {streak} in a row. Next one's yours.");
        },
        c =>
        {
            double upsets = SumOverChars(c.Chars1v1, "upsets");
            return upsets < 1 ? null : F("Giant slayer", $"You've pulled off {S(upsets)} upset win{Plural(upsets)} as the underdog.");
        },
        c =>
        {
            double chokes = SumOverChars(c.Chars1v1, "chokes");
            return chokes < 1 ? null : F("Don't get comfy", $"You've been upset {S(chokes)} time{Plural(chokes)} as the favorite.");
        },
        c =>
        {
            double tw = SumOverChars(c.Chars1v1, "tossupWins"), tl = SumOverChars(c.Chars1v1, "tossupLosses");
            if (tw + tl < 5)
            {
                return null;
            }
            return F("Coin flip", $"In even matchups you win {S(Round(tw / (tw + tl) * 100))}% of the time.");
        },
        c =>
        {
            var counts = new List<(string Char, double Games)>();
            foreach (var (_, stats) in c.Chars2v2)
            {
                foreach (var (teamChar, tc) in Obj(stats?["teammates"]))
                {
                    double games = Or0(tc?["wins"]) + Or0(tc?["losses"]);
                    int at = counts.FindIndex(e => e.Char == teamChar);
                    if (at >= 0)
                    {
                        counts[at] = (teamChar, counts[at].Games + games);
                    }
                    else
                    {
                        counts.Add((teamChar, games));
                    }
                }
            }

            var entries = counts.OrderByDescending(e => e.Games).ToList();
            if (entries.Count == 0 || entries[0].Games < 3)
            {
                return null;
            }
            return F("Duo of legend", $"Your most common 2v2 partner runs {PrettyChar(entries[0].Char)} ({S(entries[0].Games)} games together).");
        },
        c =>
        {
            int unique = c.Chars1v1.Select(e => e.Key).Concat(c.Chars2v2.Select(e => e.Key)).Distinct().Count();
            return unique < 3 ? null : F("Roster depth", $"You've played {unique} different fighters.");
        },
        c =>
        {
            var entries = c.Chars1v1.Select(e => (Char: e.Key, Upsets: Or0(e.Value?["upsets"]))).Where(e => e.Upsets >= 2).OrderByDescending(e => e.Upsets).ToList();
            if (entries.Count == 0)
            {
                return null;
            }
            return F("Secret weapon", $"You've upset {S(entries[0].Upsets)} favorites with {PrettyChar(entries[0].Char)}. Sleeper pick.");
        },
        c =>
        {
            var entries = c.Chars1v1.Select(e => (Char: e.Key, W: Or0(e.Value?["wins"]), L: Or0(e.Value?["losses"])))
                .Select(e => (e.Char, e.W, e.L, G: e.W + e.L)).Where(e => e.G >= 5).OrderByDescending(e => e.G).ToList();
            if (entries.Count == 0)
            {
                return null;
            }
            var main = entries[0];
            double pct = Round(main.W / main.G * 100);
            if (pct >= 70)
            {
                return F("Comfy pick", $"{PrettyChar(main.Char)} wins {S(pct)}% of their 1v1s. Don't mess with what works.");
            }
            if (pct <= 30)
            {
                return F("Tough love", $"Your {PrettyChar(main.Char)} win rate is {S(pct)}%. Yet you keep picking them. Respect the loyalty.");
            }
            return F("Main stat", $"{PrettyChar(main.Char)}: {S(main.W)}W-{S(main.L)}L ({S(pct)}%).");
        },
        c =>
        {
            var worst = Matchups(c).Where(e => e.Games >= 4 && e.Wr <= 0.3).OrderBy(e => e.Wr).ToList();
            if (worst.Count == 0)
            {
                return null;
            }
            return F("Kryptonite", $"{PrettyChar(worst[0].Opp)} is your worst matchup: {S(worst[0].W)}W-{S(worst[0].L)}L.");
        },
        c =>
        {
            var best = Matchups(c).Where(e => e.Games >= 4 && e.Wr >= 0.7).OrderByDescending(e => e.Wr).ToList();
            if (best.Count == 0)
            {
                return null;
            }
            return F("Free real estate", $"You bully {PrettyChar(best[0].Opp)}: {S(best[0].W)}W-{S(best[0].L)}L. They hate to see you coming.");
        },
        // === Pure jokes ===
        c =>
        {
            double walkTime = OrZero(NumberOf(c.Agg["totalWalkTime"]));
            if (walkTime < 600)
            {
                return null;
            }
            return F("Pedometer", $"You've walked for {S(Round(walkTime / 60))} minutes in combat. That's roughly {Fixed(walkTime / 300, 1)} miles of pacing.");
        },
        c => N(c.Agg["totalDamageTaken"]) > 10000
            ? F("Pain tolerance", $"{PrettyNumber(N(c.Agg["totalDamageTaken"]))} damage taken. Your mains must have good health insurance.") : null,
        c =>
        {
            double jumps = N(c.Agg["totalJumps"]), doubles = N(c.Agg["totalDoubleJumps"]);
            if (jumps > 1000 && doubles > 500)
            {
                double ratio = double.Parse(Fixed(doubles / jumps, 2), CultureInfo.InvariantCulture);
                if (ratio > 0.7)
                {
                    return F("Air conditioner", $"You double-jump after {S(Round(ratio * 100))}% of your jumps. Just buy a jetpack.");
                }
            }

            return null;
        },
        c => N(c.Agg["totalRingoutsReceived"]) > 50
            ? F("Frequent flyer", $"{PrettyNumber(N(c.Agg["totalRingoutsReceived"]))} ringouts taken. The blast zone has a rewards program and you're gold tier.") : null,
        c =>
        {
            double used = Or0(c.Agg["totalAttacksUsed"]), hit = Or0(c.Agg["totalAttacksHit"]);
            if (used < 1000)
            {
                return null;
            }
            double missed = used - hit;
            return missed / used > 0.6 ? F("Wind up", $"{PrettyNumber(missed)} of your attacks missed. That's a lot of shadowboxing.") : null;
        },
        c =>
        {
            double air = N(c.Agg["totalAirTime"]), walk = N(c.Agg["totalWalkTime"]);
            if (air > 0 && walk > 0 && air / walk > 3)
            {
                return F("Aerophile", $"You spend {Fixed(air / walk, 1)}× more time in the air than walking. Pigeons are nervous.");
            }

            return null;
        },
        // === Fighter stats ===
        c =>
        {
            // { ...chars1v1, ...chars2v2 }: a character played in both counts with its 2v2 stats, in its 1v1 place.
            var merged = new List<(string Char, JsonNode? Stats)>();
            foreach (var (k, v) in c.Chars1v1)
            {
                merged.Add((k, v));
            }

            foreach (var (k, v) in c.Chars2v2)
            {
                int at = merged.FindIndex(e => e.Char == k);
                if (at >= 0)
                {
                    merged[at] = (k, v);
                }
                else
                {
                    merged.Add((k, v));
                }
            }

            var abilities = new List<(string Char, string Name, double Count)>();
            foreach (var (character, stats) in merged)
            {
                foreach (var (name, count) in Obj(stats?["fighterStats"]))
                {
                    double total = OrZeroN(0) + NumberOf(Truthy(count) ? count : JsonValue.Create(0));
                    if (total > 0)
                    {
                        abilities.Add((character, name, total));
                    }
                }
            }

            var sorted = abilities.OrderByDescending(a => a.Count).ToList();
            if (sorted.Count == 0)
            {
                return null;
            }
            var top = sorted[0];
            if (top.Count < 3)
            {
                return null;
            }
            return F("Signature move", $"You've used {PrettyChar(top.Char)}'s {top.Name} {PrettyNumber(top.Count)} times.");
        },
        Ability("Buff", n => F("Buff banana", $"You've popped Banana Guard's buff {PrettyNumber(n)} times. Bodybuilder mode.")),
        Ability("Facebox", n => F("Slip 'n slide", $"You've banana-slipped {PrettyNumber(n)} times. Straight onto the face.")),
        Ability("SandwichHeal", n => F("Munchies", $"You've eaten {PrettyNumber(n)} Shaggy sandwich{(n == 1 ? "" : "es")}. Zoinks.")),
        Ability("EnragedAttackUsed", n => F("Like, RAGE mode", $"Shaggy's gone full rage {PrettyNumber(n)} time{Plural(n)}.")),
        Ability("FullBuff", n => F("Fusion complete", $"Garnet has fully charged {PrettyNumber(n)} times. Electric.")),
        Ability("Bubblestack", n => F("Bubble wrap", $"You've stacked Steven's bubble {PrettyNumber(n)} times.")),
        Ability("EnemyBubble", n => F("Caught you", $"You've trapped {PrettyNumber(n)} enemies in Steven's bubble.")),
        Ability("Van", n => F("Jinkies!", $"Velma's van has flattened {PrettyNumber(n)} victim{Plural(n)}.")),
        Ability("GrabAlly", n => F("Teamwork", $"Velma's grabbed a teammate {PrettyNumber(n)} times. Rescue squad.")),
        Ability("Counter", n => F("Why so serious?", $"Joker has countered {PrettyNumber(n)} time{Plural(n)}. Nailed the timing.")),
        Ability("RageHit", n => F("Unstoppable force", $"{PrettyNumber(n)} rage hits with Jason. Horror movie material.")),
        Ability("BombSave", n => F("Bombastic save", $"Harley's bomb has bailed your teammate out {PrettyNumber(n)} times.")),
        Ability("RageMission", n => F("I am... weapon", $"You've triggered Iron Giant's rage {PrettyNumber(n)} times.")),
        Ability("Eat", n => F("Nom nom", $"Taz has eaten {PrettyNumber(n)} opponent{Plural(n)}. Dietary concerns.")),
        Ability("Powerslide", n => F("Bass drop", $"Marceline's powerslid {PrettyNumber(n)} time{Plural(n)}. Low-key stylish.")),
        Ability("Silence", n => F("Hush now", $"Agent Smith has silenced {PrettyNumber(n)} opponent{Plural(n)}. The Matrix has you.")),
        Ability("TeleAlly", n => F("Paging Mr. Anderson", $"Agent Smith's teleported a teammate {PrettyNumber(n)} times.")),
        Ability("Pass", n => F("MVP assist", $"{PrettyNumber(n)} LeBron passes landed. Court vision.")),
        Ability("Defense", n => F("Rim protector", $"{PrettyNumber(n)} defensive stop{Plural(n)} with LeBron.")),
        Ability("BuyBmo", n => F("Mathematical", $"Finn's bought BMO {PrettyNumber(n)} time{Plural(n)}. Big spender.")),
        // The game's typo: "AirPojectileDestroy".
        Ability("AirPojectileDestroy", n => F("Swat flies", $"Finn's swatted {PrettyNumber(n)} projectile{Plural(n)} out of the air.")),
        Ability("Electric", n => F("Zap!", $"{PrettyNumber(n)} electric batarang{Plural(n)} with Batman. Shocking.")),
        Ability("GrappleAlly", n => F("I'm Batman", $"Batman's grappled to a teammate {PrettyNumber(n)} times. Dynamic duo.")),
        Ability("PortalKB", n => F("Wubba lubba", $"{PrettyNumber(n)} portal knockback{Plural(n)}. Dimension-hopping dirty work.")),
        Ability("AllySeed", n => F("Plumbus care", $"Rick has buffed a teammate {PrettyNumber(n)} times with a seed.")),
        Ability("SplitNade", n => F("Aw jeez", $"Morty has split {PrettyNumber(n)} grenade{Plural(n)}. Risky moves.")),
        Ability("AllySave", n => F("Sidekick energy", $"Morty's bailed out a teammate {PrettyNumber(n)} times.")),
        Ability("LoveLeashAlly", n => F("Heartstrings", $"You've love-leashed your teammate {PrettyNumber(n)} times. Co-op goals.")),
        Ability("Teleport", n => F("Say my name", $"Beetlejuice has teleported {PrettyNumber(n)} time{Plural(n)}. It's showtime.")),
        Ability("ProjectileShield", n => F("Godly defense", $"Black Adam has shielded {PrettyNumber(n)} projectile{Plural(n)}. Shazam who?")),
        Ability("ReverseProjectile", n => F("Return to sender", $"Marvin has reversed {PrettyNumber(n)} projectile{Plural(n)}.")),
        Ability("bubble", n => F("Martian contained", $"Marvin has bubbled {PrettyNumber(n)} opponent{Plural(n)}.")),
        Ability("Kickflip", n => F("Gnarly", $"Stripe has landed {PrettyNumber(n)} kickflip{Plural(n)}. Shred it.")),
        Ability("AllyAttach", n => F("Piggyback", $"Jerry has latched onto a teammate {PrettyNumber(n)} times.")),
        Ability("lenoregrab", n => F("Ghostly", $"Lenore has grabbed {PrettyNumber(n)} opponent{Plural(n)}.")),
        // === ELO ===
        c =>
        {
            if (!Truthy(c.Elo["elo_1v1"]))
            {
                return null;
            }
            double wins = Or0(c.Elo["wins_1v1"]), losses = Or0(c.Elo["losses_1v1"]);
            if (wins + losses < 5)
            {
                return null;
            }
            return F("Rating check", $"Your 1v1 rating is {Template(c.Elo["elo_1v1"])}. ({S(wins)}W-{S(losses)}L)");
        },
        // === Recent-match highlights ===
        c =>
        {
            double best = 0;
            JsonNode? bestChar = null;
            foreach (var p in RecentPlayers(c))
            {
                if (Same(p["accountId"], c.Stats["account_id"]) && Or0(p["damage"]) > best)
                {
                    best = N(p["damage"]);
                    bestChar = p["character"];
                }
            }

            return best < 50 ? null : F("Heavy hitter", $"Your best damage in a recent game is {S(best)} with {PrettyChar(Text(bestChar))}.");
        },
        c =>
        {
            double best = 0;
            foreach (var p in RecentPlayers(c))
            {
                if (Same(p["accountId"], c.Stats["account_id"]) && Or0(p["ringouts"]) > best)
                {
                    best = N(p["ringouts"]);
                }
            }

            return best < 2 ? null : F("Clean sweep", $"You've put up {S(best)} ringouts in a single recent game.");
        },
    ];

    private static Func<Ctx, FunFact?> Ability(string name, Func<double, FunFact> fact) => c =>
    {
        double count = FighterStat(c, name);
        return count < 1 ? null : fact(count);
    };

    // findFighterStat: the ability's count summed over every character, 1v1 and 2v2.
    private static double FighterStat(Ctx c, string name)
    {
        double total = 0;
        foreach (var (_, stats) in c.Chars1v1.Concat(c.Chars2v2))
        {
            var count = stats?["fighterStats"]?[name];
            total += NumberOf(Truthy(count) ? count : JsonValue.Create(0));
        }

        return total;
    }

    private static IEnumerable<(string Opp, double Wr, double Games, double W, double L)> Matchups(Ctx c)
    {
        var merged = new List<(string Opp, double W, double L)>();
        foreach (var (_, stats) in c.Chars1v1)
        {
            foreach (var (opp, rec) in Obj(stats?["matchups"]))
            {
                int at = merged.FindIndex(e => e.Opp == opp);
                var (w, l) = at >= 0 ? (merged[at].W, merged[at].L) : (0, 0);
                var entry = (opp, w + Or0(rec?["wins"]), l + Or0(rec?["losses"]));
                if (at >= 0)
                {
                    merged[at] = entry;
                }
                else
                {
                    merged.Add(entry);
                }
            }
        }

        return merged.Select(e => (e.Opp, e.W / Math.Max(e.W + e.L, 1), e.W + e.L, e.W, e.L));
    }

    private static IEnumerable<JsonObject> RecentPlayers(Ctx c) =>
        c.Recent1v1.Concat(c.Recent2v2).SelectMany(m => m?["players"] is JsonArray players ? players.OfType<JsonObject>() : []);

    private static double SumOverChars(JsonObject map, string field) => map.Sum(e => Or0(e.Value?[field]));

    // ── JavaScript semantics ─────────────────────────────────────────────────────────────────────────────────────

    // A value as a number (the generators' operands are numbers or missing): NaN when it is not one.
    private static double N(JsonNode? v) => v is JsonValue j && Num(j) is { } d ? d : double.NaN;

    // The number in a value, whatever it was made from (a parsed document, or an int written here).
    private static double? Num(JsonValue j) =>
        j.TryGetValue(out double d) ? d : j.TryGetValue(out int i) ? i : j.TryGetValue(out long l) ? l : j.TryGetValue(out decimal m) ? (double)m : null;

    // Number(v): missing is NaN, null 0, a numeric string its number.
    private static double NumberOf(JsonNode? v) => v switch
    {
        null => 0,
        JsonValue j when Num(j) is { } d => d,
        JsonValue j when j.TryGetValue(out bool b) => b ? 1 : 0,
        JsonValue j when j.TryGetValue(out string? s) => Js.Number(s),
        _ => double.NaN,
    };

    // v || 0 for a number.
    private static double Or0(JsonNode? v) => OrZero(N(v));

    private static double OrZero(double d) => double.IsNaN(d) || d == 0 ? 0 : d;

    private static double OrZeroN(double d) => OrZero(d);

    private static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonValue j when j.TryGetValue(out bool b) => b,
        JsonValue j when Num(j) is { } d => d != 0 && !double.IsNaN(d),
        JsonValue j when j.TryGetValue(out string? s) => s.Length > 0,
        _ => true,
    };

    // a === b for the ids the documents hold (strings).
    private static bool Same(JsonNode? a, JsonNode? b) => a is JsonValue x && b is JsonValue y && x.TryGetValue(out string? s) && y.TryGetValue(out string? t) && s == t;

    private static string? Text(JsonNode? v) => v is JsonValue j && j.TryGetValue(out string? s) ? s : null;

    // Math.round: halves up.
    private static double Round(double x) => Math.Floor(x + 0.5);

    // A number as a template literal writes it.
    private static string S(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Js.Stringify(JsonValue.Create(d));

    private static string Template(JsonNode? v) => v is JsonValue j && j.TryGetValue(out string? s) ? s : v is JsonValue n && n.TryGetValue(out double d) ? S(d) : Js.Stringify(v);

    // Number.prototype.toFixed: the n for which n / 10^digits is closest to the exact value of d, the larger n on a tie
    // (0.625.toFixed(2) is "0.63", where .NET's "F2" rounds the tie to even).
    internal static string Fixed(double d, int digits)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (Math.Abs(d) >= 1e21 || double.IsInfinity(d))
        {
            return S(d);
        }

        if (d < 0)
        {
            string positive = Fixed(-d, digits);
            return positive.Trim('0', '.').Length == 0 ? positive : "-" + positive;
        }

        long bits = BitConverter.DoubleToInt64Bits(d);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        // d = mantissa * 2^(exponent - 1075) exactly.
        var scaled = new System.Numerics.BigInteger(mantissa) * System.Numerics.BigInteger.Pow(10, digits);
        int shift = exponent - 1075;
        System.Numerics.BigInteger n;
        if (shift >= 0)
        {
            n = scaled << shift;
        }
        else
        {
            var denominator = System.Numerics.BigInteger.One << -shift;
            n = System.Numerics.BigInteger.DivRem(scaled, denominator, out var remainder);
            if (remainder * 2 >= denominator)
            {
                n += 1;
            }
        }

        string text = n.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        return digits == 0 ? text : $"{text[..^digits]}.{text[^digits..]}";
    }

    private static string Plural(double n) => n == 1 ? "" : "s";

    private static string PrettyNumber(double n)
    {
        if (!double.IsFinite(n) || n <= 0)
        {
            return "0";
        }
        if (n >= 1_000_000)
        {
            return $"{Fixed(n / 1_000_000, 1)}M";
        }
        if (n >= 10_000)
        {
            return $"{S(Math.Floor(n / 1000))}K";
        }
        return Round(n).ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return "0 seconds";
        }
        if (seconds < 60)
        {
            return $"{S(Round(seconds))} seconds";
        }
        double minutes = Math.Floor(seconds / 60);
        if (minutes < 60)
        {
            return $"{S(minutes)} minute{Plural(minutes)}";
        }
        double hours = Math.Floor(minutes / 60), rem = minutes % 60;
        return rem > 0 ? $"{S(hours)}h {S(rem)}m" : $"{S(hours)} hour{Plural(hours)}";
    }

    // The game's DisplayName, else the slug made readable; a C-code with no name stays as it is.
    public static string PrettyChar(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return "Unknown";
        }
        if (s_names.TryGetValue(slug, out string? name))
        {
            return name;
        }
        string clean = slug.StartsWith("character_", StringComparison.Ordinal) ? slug["character_".Length..] : slug;
        if (CCode().IsMatch(clean))
        {
            return clean;
        }
        return WordStart().Replace(clean.Replace('_', ' '), m => m.Value.ToUpperInvariant());
    }

    [GeneratedRegex(@"^[cC]\d")]
    private static partial Regex CCode();

    // \b\w as JavaScript reads it (ASCII word characters).
    [GeneratedRegex(@"\b\w", RegexOptions.ECMAScript)]
    private static partial Regex WordStart();

    private static FunFact F(string title, string message) => new(title, message);

    private static JsonObject Obj(JsonNode? v) => v as JsonObject ?? [];

    private static JsonArray Arr(JsonNode? v) => v as JsonArray ?? [];

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(FunFactRules).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.FunFacts.character-names.json")
            ?? throw new InvalidOperationException("character-names.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal);
    }
}

public static class FunFactsHosting
{
    public static IServiceCollection AddFunFacts(this IServiceCollection services) => services.AddSingleton<IFunFacts, FunFactsService>();
}
