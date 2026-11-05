using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.FunFacts;

namespace OpenVersus.Server.Core.Tests.FunFacts;

/// <summary>The fun facts (FunFactRules), against the TS server's generators.</summary>
public sealed class FunFactsTests
{
    private static List<FunFact> Pool(string stats, string? elo = null) =>
        FunFactRules.Pool((JsonObject)Js.Parse(stats)!, elo is null ? null : (JsonObject)Js.Parse(elo)!);

    private static FunFact? Titled(List<FunFact> pool, string title) => pool.FirstOrDefault(f => f.Title == title);

    /// <summary>
    /// Every real player's pool, fact for fact, as the TS server makes it: FUNFACTS_GOLDEN names the file
    /// tools/funfacts/funfacts_pools.mjs writes (players' data, kept local). Without it the test has nothing to read.
    /// </summary>
    [Fact]
    public void FunFactsMatchTheTsServer()
    {
        string? golden = Environment.GetEnvironmentVariable("FUNFACTS_GOLDEN");
        if (string.IsNullOrEmpty(golden))
        {
            return;
        }

        int players = 0;
        var wrong = new List<string>();
        foreach (string line in File.ReadLines(golden))
        {
            var entry = (JsonObject)Js.Parse(line)!;
            var expected = entry["pool"]!.AsArray().Select(f => new FunFact(f!["title"]!.GetValue<string>(), f["message"]!.GetValue<string>())).ToList();
            var actual = FunFactRules.Pool(entry["stats"]!.AsObject(), entry["elo"] as JsonObject);
            players++;
            if (!expected.SequenceEqual(actual))
            {
                var missing = expected.Except(actual).Select(f => $"-{f.Title}: {f.Message}");
                var extra = actual.Except(expected).Select(f => $"+{f.Title}: {f.Message}");
                wrong.Add($"{entry["account_id"]}: {string.Join(" | ", missing.Concat(extra).DefaultIfEmpty("(order)"))}");
            }
        }

        Assert.True(players > 0);
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {players} players differ:\n{string.Join("\n", wrong.Take(15))}");
    }

    [Fact]
    public void NoStatsSayNothing() => Assert.Empty(Pool("{}"));

    // { ...chars1v1 } merged with the 2v2 entries as (a || 0) + b || 0: a 2v2 entry with no wins zeroes the character.
    [Fact]
    public void TheMostPlayedCountKeepsTheTsServersPrecedence()
    {
        var pool = Pool("""
            {"characters_1v1": {"character_batman": {"wins": 9, "losses": 1}, "character_taz": {"wins": 2, "losses": 2}},
             "characters_2v2": {"character_batman": {"losses": 1}}}
            """);
        Assert.Equal("Taz is your most-played fighter (4 games).", Titled(pool, "Main character")!.Message);
    }

    [Fact]
    public void NumbersAreWrittenAsTheTsServerWritesThem()
    {
        var pool = Pool("""{"aggregate": {"totalRingouts": 0, "totalRingoutsReceived": 12, "totalDamageDodged": 1234, "totalDamageTaken": 2500000, "totalCrouchTime": 3725}}""");
        // 1 / 0 is Infinity, and toFixed writes it so.
        Assert.Equal("You get rung out Infinity× more than you ring out. Ouch.", Titled(pool, "Splat math")!.Message);
        Assert.Equal("You've dodged 1,234 damage. Money saved.", Titled(pool, "Matrix mode")!.Message);
        Assert.Equal("2.5M damage taken. Your mains must have good health insurance.", Titled(pool, "Pain tolerance")!.Message);
        Assert.Equal("You've crouched for 1h 2m. Turtle activity detected.", Titled(pool, "Crouched up")!.Message);
    }

    // Number.prototype.toFixed: a tie goes to the larger neighbour, on the double's exact value.
    [Theory]
    [InlineData(0.625, 2, "0.63")]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(2.5, 0, "3")]
    [InlineData(0.05, 1, "0.1")]
    [InlineData(7.25, 1, "7.3")]
    [InlineData(1234.5678, 2, "1234.57")]
    [InlineData(0.0, 2, "0.00")]
    [InlineData(1e-7, 2, "0.00")]
    public void ToFixedIsJavaScripts(double value, int digits, string expected) => Assert.Equal(expected, FunFactRules.Fixed(value, digits));

    [Fact]
    public void CharactersAreNamedAsTheGameNamesThem()
    {
        Assert.Equal("Wonder Woman", FunFactRules.PrettyChar("character_wonder_woman"));
        Assert.Equal("C099", FunFactRules.PrettyChar("character_C099"));
        Assert.Equal("Some New Fighter", FunFactRules.PrettyChar("character_some_new_fighter"));
        Assert.Equal("Unknown", FunFactRules.PrettyChar(null));
    }
}
