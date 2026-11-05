using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Matches;

// Who won a game, from every end-of-match report its humans sent (submit_end_of_match_stats; his rule, 2026-10-05):
// the players decide; when they disagree, the spectators do (a spectator only receives and acknowledges inputs, so its
// simulation is the definitive one; their majority, a tie going to the first of them to report); with no spectator
// (every ranked set), the first player to report, as the TS server took the first report of all. A report from someone
// the match's config does not name counts as a player's. No report with a winner: none.

/// <summary>One human's report of a game: in arrival order, whether they spectated, the winner they saw (null: none).</summary>
public sealed record MatchReport(string PlayerId, long Order, bool Spectator, int? Winner);

public static class MatchWinner
{
    public static int? Resolve(IEnumerable<MatchReport> reports)
    {
        var ordered = reports.Where(r => r.Winner is not null).OrderBy(r => r.Order).ToList();
        var players = ordered.Where(r => !r.Spectator).ToList();
        var spectators = ordered.Where(r => r.Spectator).ToList();
        if (players.Count > 0 && players.All(r => r.Winner == players[0].Winner))
        {
            return players[0].Winner;
        }

        if (spectators.Count > 0)
        {
            // The most reported winner; between equally reported ones, the one reported first.
            return spectators.GroupBy(r => r.Winner).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(r => r.Order)).First().Key;
        }

        return players.FirstOrDefault()?.Winner;
    }

    /// <summary>The winner a report claims (EndOfMatchStats.WinningTeamIndex): a whole number, as sent or as Hydra's
    /// whole-number double ({_hydra_double: n}); null for anything else.</summary>
    public static int? Claimed(JsonNode? stats) => Number(stats?["WinningTeamIndex"]) is { } n && n == Math.Floor(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    /// <summary>A number, unwrapped from Hydra's whole-number double; null when it is neither.</summary>
    public static double? Number(JsonNode? value) => value switch
    {
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            double.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture),
        JsonObject { Count: 1 } o when o["_hydra_double"] is JsonValue d => Number(d),
        _ => null,
    };
}
