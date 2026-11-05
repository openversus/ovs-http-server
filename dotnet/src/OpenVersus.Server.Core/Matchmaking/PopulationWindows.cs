using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matchmaking;

// Ranked 1v1's skill window by population (Tuggernuts, 2026-10-26: "our ELO-based matchmaking change"). The fixed window
// (250 under 5 s waiting, 500 under 10 s, anyone after) put a 998-RP player against a 0-RP one after 10 s whenever the
// queue was thin, and it was thin almost always: prod's queue held 2 tickets at 93% of the 2 s looks (2026-10-06..08).
// What decides whether waiting is worth it is how many players near your RP are available now or soon:
//   pool    the solo tickets in the 1v1 queue within NearRp of the ticket (itself included), plus the players of the
//           live ranked 1v1 sets that will be back soon (BackSoonAfterGames: past their first game, in game 2 or 3)
//           within NearRp of it
//           (their RP: the set's first game's match record, match:{set}; a player whose RP is gone counts as near)
//   tier    Quiet below MediumFrom, Medium below BusyFrom, else Busy; each has its own steps
//   window  a tier's steps, "seconds:rp,...": the RP gap allowed while the wait is under each step's seconds, anyone
//           after the last (its cap)
// A pair matches when the gap fits the stricter of their two windows, or when either has waited its tier's cap (without
// that, a capped player waited for a newcomer's window to open too; replayed on prod's logs, p90 waits reached ~100 s).
// Quiet's default steps are the fixed window's, so a thin queue behaves as before. 2v2 (and so Twosday), Casual and
// Testing Grounds keep the fixed window. Replayed on prod's September log (Sep 11 - Oct 1 2026, 12,018 ranked 1v1
// matches), these defaults (NearRp 500, every live set back soon) matched within one rank 25% -> 29% of the time (29%
// -> 42% with twice the players, the Season 1 launch's estimate), for a median wait of 10 s -> 11 s (10.5 s -> 16 s
// with twice the players) and a p90 of 19 s -> 31 s; 17% of the waits passed 30 s. October's logs (Oct 6-26) agree,
// 28% -> 36% and 24% -> 30%. A nearer pool (300) that counts a set from its second game on gave half the gain.
// Off (Matchmaking:Population:Enabled false, changeable while running): the fixed window, as before.

/// <summary>Ranked 1v1's population-based skill window (Matchmaking:Population).</summary>
public sealed class MatchmakingPopulationSettings : IValidatableObject
{
    [Description("Ranked 1v1's skill window follows how many players near the ticket's RP are available now or soon (the 1v1 queue, and the live ranked 1v1 sets past their first game). Off: the fixed window (250 under 5 s waiting, 500 under 10 s, anyone after), as before. Changeable while running: turn it off here if matches misbehave.")]
    public bool Enabled { get; set; } = true;

    [Description("Players within this many RP of a ticket count toward its pool (a rank is 100 RP; 500 is a tier).")]
    [Range(0, 10000)]
    public double NearRp { get; set; } = 500;

    [Description("A live ranked 1v1 set's players count as back soon once it has played this many games (0: every live set; 1: in game 2 or 3). Fewer means bigger pools, closer matches and longer waits.")]
    [Range(0, 2)]
    public int BackSoonAfterGames { get; set; }

    [Description("A pool of at least this many players is Medium (below it, Quiet).")]
    [Range(1, 1000)]
    public int MediumFrom { get; set; } = 6;

    [Description("A pool of at least this many players is Busy.")]
    [Range(1, 1000)]
    public int BusyFrom { get; set; } = 16;

    [Description("Quiet's steps, \"seconds:rp,...\": the RP gap allowed while the wait is under each step's seconds; anyone after the last. The default is the fixed window's.")]
    public string Quiet { get; set; } = "5:250,10:500";

    [Description("Medium's steps (see Quiet).")]
    public string Medium { get; set; } = "10:100,20:200,30:400";

    [Description("Busy's steps (see Quiet).")]
    public string Busy { get; set; } = "30:100,45:200,60:300";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BusyFrom < MediumFrom)
        {
            yield return new ValidationResult("BusyFrom must be at least MediumFrom", [nameof(BusyFrom)]);
        }

        foreach (var (name, text) in new[] { (nameof(Quiet), Quiet), (nameof(Medium), Medium), (nameof(Busy), Busy) })
        {
            if (PopulationWindows.Parse(text) is null)
            {
                yield return new ValidationResult($"{name} must be \"seconds:rp,...\" with the seconds rising", [name]);
            }
        }
    }
}

/// <summary>A tier of the population window.</summary>
public enum PopulationTier
{
    Quiet,
    Medium,
    Busy,
}

/// <summary>Ranked 1v1's population-based skill windows: the rules, apart from Redis.</summary>
internal static class PopulationWindows
{
    /// <summary>"seconds:rp,..." with the seconds rising; null when it is not.</summary>
    internal static IReadOnlyList<(double Seconds, double Rp)>? Parse(string? text)
    {
        var steps = new List<(double Seconds, double Rp)>();
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':');
            if (pair.Length != 2
                || !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double rp)
                || seconds <= 0 || rp < 0 || (steps.Count > 0 && seconds <= steps[^1].Seconds))
            {
                return null;
            }

            steps.Add((seconds, rp));
        }

        return steps.Count > 0 ? steps : null;
    }

    internal static PopulationTier TierOf(int pool, MatchmakingPopulationSettings settings) =>
        pool >= settings.BusyFrom ? PopulationTier.Busy : pool >= settings.MediumFrom ? PopulationTier.Medium : PopulationTier.Quiet;

    internal static IReadOnlyList<(double Seconds, double Rp)> StepsOf(PopulationTier tier, MatchmakingPopulationSettings settings) =>
        Parse(tier switch
        {
            PopulationTier.Busy => settings.Busy,
            PopulationTier.Medium => settings.Medium,
            _ => settings.Quiet,
        }) ?? [(5, 250), (10, 500)];

    /// <summary>The RP gap allowed after waiting <paramref name="seconds"/>: the first step not yet reached, else anyone.</summary>
    internal static double Window(IReadOnlyList<(double Seconds, double Rp)> steps, double seconds)
    {
        foreach (var (until, rp) in steps)
        {
            if (seconds < until)
            {
                return rp;
            }
        }

        return double.PositiveInfinity;
    }

    /// <summary>The wait after which a ticket matches anyone: the last step's seconds.</summary>
    internal static double Cap(IReadOnlyList<(double Seconds, double Rp)> steps) => steps[^1].Seconds;

    /// <summary>One side of a pair: its tier's steps and how long it has waited.</summary>
    internal readonly record struct Side(PopulationTier Tier, IReadOnlyList<(double Seconds, double Rp)> Steps, double Waited)
    {
        public double Window => PopulationWindows.Window(Steps, Waited);

        public bool Capped => Waited >= Cap(Steps);
    }

    /// <summary>Whether two tickets <paramref name="gap"/> RP apart match: either capped, or within the stricter window.</summary>
    internal static bool Allows(Side a, Side b, double gap) => a.Capped || b.Capped || gap <= Math.Min(a.Window, b.Window);

    /// <summary>A ticket's pool: the searching skills and the back-soon skills within NearRp of it (NaN: unknown, counted).</summary>
    internal static int Pool(double skill, IEnumerable<double> searching, IEnumerable<double> backSoon, double nearRp) =>
        searching.Count(s => Math.Abs(s - skill) <= nearRp) + backSoon.Count(s => double.IsNaN(s) || Math.Abs(s - skill) <= nearRp);

    /// <summary>
    /// The skills of the players who will be back in the 1v1 queue soon: those of the live ranked 1v1 sets that have
    /// played at least <paramref name="afterGames"/> games (ranked_set:{set} gamesPlayed), each player's RP from the
    /// set's first game's match record (match:{set}, its tickets as queued); NaN for a player whose RP is gone.
    /// </summary>
    internal static async Task<List<double>> BackSoonAsync(IDatabase redis, int afterGames)
    {
        var sets = new List<string>();
        long cursor = 0;
        do
        {
            var reply = (RedisResult[])(await redis.ExecuteAsync("SCAN", cursor.ToString(CultureInfo.InvariantCulture), "MATCH", "ranked_set:*", "COUNT", "1000"))!;
            cursor = long.Parse((string)reply[0]!, CultureInfo.InvariantCulture);
            sets.AddRange(((RedisResult[])reply[1]!).Select(k => (string)k!));
        }
        while (cursor != 0);

        var skills = new List<double>();
        if (sets.Count == 0)
        {
            return skills;
        }

        var states = await redis.StringGetAsync([.. sets.Select(k => new RedisKey(k))]);
        var live = new List<(string Set, int Players)>();
        for (int i = 0; i < sets.Count; i++)
        {
            if (Json(states[i]) is not JsonObject set || Text(set["mode"]) != "1v1" || !(Count(set["gamesPlayed"]) >= afterGames))
            {
                continue;
            }

            live.Add((sets[i]["ranked_set:".Length..], (set["players"] as JsonArray)?.Count ?? 2));
        }

        if (live.Count == 0)
        {
            return skills;
        }

        var matches = await redis.StringGetAsync([.. live.Select(s => new RedisKey($"match:{s.Set}"))]);
        for (int i = 0; i < live.Count; i++)
        {
            var known = (Json(matches[i])?["tickets"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(t => (t["players"] as JsonArray ?? []).OfType<JsonObject>())
                .Select(p => Count(p["skill"]))
                .ToList();
            skills.AddRange(known.Count > 0 ? known : Enumerable.Repeat(double.NaN, live[i].Players));
        }

        return skills;
    }

    private static JsonNode? Json(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse((string)value!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Count(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.NaN;
}
