using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Leaderboards;

// Whether a result changes anyone's rating: the one check every C# rating path asks (docs/MIGRATION-BRIDGES.md 6).
// Ratings are for the regular 1v1 and 2v2 queues only, ranked sets included: never a rift, a custom lobby or Casual
// (Casual may get a rating of its own, kept apart), and never a bot. The TS server asked this in three different ways
// (its match result skipped a match:{id} with isPasswordMatch, its dodge and disconnect paths a config with
// isCustomGame, and none of them left bots out), so a rift match was rated by two of them.
//
// What it reads: the mode and players (a match config's, or a set's, which are its first game's), and the match
// (match:{id}) and its config ({id}) when they are still there. Every match C# starts that is not from a regular queue
// has isPasswordMatch (MatchLauncher; the Casual queue), and a custom lobby's and the Casual queue's configs also have
// isCustomGame.

public static class RatedMatches
{
    /// <summary>
    /// Null when a match (or set) of <paramref name="mode"/> between <paramref name="players"/> (a config's entries)
    /// changes ratings; otherwise why not. <paramref name="match"/> (match:{id}) and <paramref name="config"/> ({id}) are
    /// checked when given.
    /// </summary>
    public static string? WhyNotRated(string? mode, JsonArray? players, JsonObject? match = null, JsonObject? config = null)
    {
        if (mode is null || !(mode.StartsWith("1v1", StringComparison.Ordinal) || mode.StartsWith("2v2", StringComparison.Ordinal)))
        {
            return $"mode {mode ?? "(none)"} is not rated";
        }

        if (players is null || players.Count == 0)
        {
            return "no players";
        }

        if (players.Any(p => Truthy(p?["isBot"])))
        {
            return "a bot played";
        }

        if (!players.Any(p => Team(p) == 0) || !players.Any(p => Team(p) == 1))
        {
            return "a team has no players";
        }

        if (Truthy(match?["isPasswordMatch"]))
        {
            return "a password match (custom lobby, rift or Casual)";
        }

        if (Truthy(config?["isCustomGame"]))
        {
            return "a custom game";
        }

        return null;
    }

    private static double? Team(JsonNode? player) => player?["teamIndex"] is JsonValue v ? Number(v) : null;

    // A number however the node holds it (parsed, or created from an int, long or double).
    private static double? Number(JsonValue v) =>
        v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    // JavaScript truthiness of a flag (true is what is written).
    private static bool Truthy(JsonNode? value) => value is JsonValue v && v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.Number => Number(v) is { } d && d != 0 && !double.IsNaN(d),
        System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length > 0,
        _ => false,
    };
}
