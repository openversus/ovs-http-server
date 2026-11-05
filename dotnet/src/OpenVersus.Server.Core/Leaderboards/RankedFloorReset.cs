using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Leaderboards;

// A ranked season's rating reset (ovsctl ranked floor-reset; Tuggernuts, 2026-10-26): every rating goes back to the
// bottom of its tier, not to the default. The TS server's seasons wiped the ratings (every player unrated again): the
// first week's queue paired by arrival order, the ladder re-sorted itself from nothing, and a Gold met whoever queued
// next. Here a Gold 1 at 1,499 starts the season a Gold 5 at 1,000, a Platinum at 1,500, a Diamond at 2,000, and so on:
// something to climb again, and still matched with their own kind on day one.
//   tiers        Bronze under 500, Silver 500, Gold 1000, Platinum 1500, Diamond 2000, Master 2500, Grandmaster 3000 and
//                up (the game's, eloToTierDivision in the TS eloService); a tier's floor is its first rating
//   min sets     a character (or mode rating) with fewer than MinSets sets goes to Ranked:DefaultElo instead: one or two
//                sets of a provisional K are noise, and most of them sit at 976-999 after one lost set, which the
//                floor rule would send to Silver
//   under 500    untouched (Bronze stays Bronze; it takes 21 straight losses from 1,000 to get there)
//   kept         wins, losses and streaks: an established player keeps the regular K (SetRatings) instead of swinging a
//                provisional one again; updated_at, the leaderboard's names, the match stats
// Each character's rating (eloratings characters_{mode}.{slug}.elo, with its own wins and losses) is reset on its own,
// and the mode's rating (elo_{mode}, which SetRatings keeps at the best character's) becomes the best of the reset
// characters, or its own reset when the player has no rated character in the mode. Character slugs are read and
// written exactly as stored (the game's casing). Nothing is written unless asked (apply): the dry run reports what
// would happen. Not reversible: back up eloratings first (deploy/README.md).

/// <summary>One mode's part of a floor reset: how many ratings went where.</summary>
/// <param name="Mode">1v1 or 2v2.</param>
/// <param name="Floored">By floor ("1000", "1500", ...): the character ratings lowered to it.</param>
/// <param name="ToDefault">The character ratings sent to the default (fewer than MinSets sets).</param>
/// <param name="Unchanged">The character ratings left alone: under 500, or already at their floor or the default.</param>
/// <param name="ModeFloored">The mode ratings (elo_{mode}) lowered to a floor, and</param>
/// <param name="ModeToDefault">sent to the default.</param>
public sealed record RankedFloorResetMode(string Mode, IReadOnlyDictionary<string, long> Floored, long ToDefault, long Unchanged, long ModeFloored, long ModeToDefault);

/// <summary>A floor reset, done or dry-run: the settings it ran with, how many players it read and would change, and each mode's counts.</summary>
public sealed record RankedFloorResetView(bool Applied, int MinSets, double DefaultElo, long Players, long PlayersChanged, IReadOnlyList<RankedFloorResetMode> Modes);

/// <summary>The season's rating reset: every rating to the bottom of its tier.</summary>
public static class RankedFloorReset
{
    /// <summary>The first rating of each tier from Silver up; under the first, Bronze.</summary>
    public static readonly double[] Floors = [500, 1000, 1500, 2000, 2500, 3000];

    /// <summary>The most sets a rating can be asked to have before it counts (the min-sets rule).</summary>
    public const int MaxMinSets = 1000;

    /// <summary>The bottom of <paramref name="elo"/>'s tier: the highest floor not above it; null for Bronze (under 500).</summary>
    public static double? FloorOf(double elo)
    {
        double? floor = null;
        foreach (double f in Floors)
        {
            if (elo >= f)
            {
                floor = f;
            }
        }

        return floor;
    }

    /// <summary>
    /// A rating after the reset: itself under 500; <paramref name="defaultElo"/> with fewer than <paramref name="minSets"/>
    /// sets; else the bottom of its tier. NaN (not a rating) stays NaN.
    /// </summary>
    public static double Reset(double elo, long sets, int minSets, double defaultElo) =>
        double.IsNaN(elo) || FloorOf(elo) is not { } floor ? elo
        : sets < minSets ? defaultElo
        : floor;

    private static readonly string[] s_modes = ["1v1", "2v2"];

    /// <summary>
    /// Resets every rating in <paramref name="ratings"/> (eloratings) as the header says; writes only with
    /// <paramref name="apply"/>. The counts either way.
    /// </summary>
    public static async Task<RankedFloorResetView> RunAsync(IMongoCollection<BsonDocument> ratings, int minSets, double defaultElo, bool apply, ILogger log, CancellationToken ct)
    {
        var tallies = s_modes.ToDictionary(m => m, _ => new Tally());
        long players = 0, changed = 0;
        var writes = new List<WriteModel<BsonDocument>>();
        using var cursor = await ratings.Find(FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var rating in cursor.Current)
            {
                players++;
                var set = new BsonDocument();
                foreach (string mode in s_modes)
                {
                    ResetMode(rating, mode, minSets, defaultElo, tallies[mode], set);
                }

                if (set.ElementCount == 0)
                {
                    continue;
                }

                changed++;
                if (apply)
                {
                    writes.Add(new UpdateOneModel<BsonDocument>(new BsonDocument("_id", rating["_id"]), new BsonDocument("$set", set)));
                    if (writes.Count >= 500)
                    {
                        await ratings.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
                        writes.Clear();
                    }
                }
            }
        }

        if (apply && writes.Count > 0)
        {
            await ratings.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
        }

        var view = new RankedFloorResetView(apply, minSets, defaultElo, players, changed,
            [.. s_modes.Select(m => new RankedFloorResetMode(m, tallies[m].Floored.OrderBy(f => double.Parse(f.Key, System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(f => f.Key, f => f.Value),
                tallies[m].ToDefault, tallies[m].Unchanged, tallies[m].ModeFloored, tallies[m].ModeToDefault))]);
        log.LogInformation("Ranked floor reset {Applied} (min sets {MinSets}, default {Default}): {Players} players read, {Changed} changed; {Modes}",
            apply ? "applied" : "dry run", minSets, defaultElo, players, changed,
            string.Join("; ", view.Modes.Select(m => $"{m.Mode}: {string.Join(", ", m.Floored.Select(f => $"{f.Value} to {f.Key}"))}, {m.ToDefault} to the default, {m.Unchanged} unchanged, mode ratings {m.ModeFloored} floored and {m.ModeToDefault} to the default")));
        return view;
    }

    private sealed class Tally
    {
        public Dictionary<string, long> Floored { get; } = [];
        public long ToDefault, Unchanged, ModeFloored, ModeToDefault;
    }

    // One mode of one player: each character's rating, then the mode's. The changes go into set ($set's document).
    private static void ResetMode(BsonDocument rating, string mode, int minSets, double defaultElo, Tally tally, BsonDocument set)
    {
        string field = $"characters_{mode}";
        double best = double.NaN;
        if (rating.TryGetValue(field, out var characters) && characters is BsonDocument map)
        {
            foreach (var character in map)
            {
                if (character.Value is not BsonDocument stats || !stats.TryGetValue("elo", out var elo) || !elo.IsNumeric)
                {
                    continue;
                }

                double before = elo.ToDouble();
                double after = Reset(before, Count(stats, "wins") + Count(stats, "losses"), minSets, defaultElo);
                if (!double.IsNaN(after) && (double.IsNaN(best) || after > best))
                {
                    best = after;
                }

                if (after == before || double.IsNaN(after))
                {
                    tally.Unchanged++;
                    continue;
                }

                if (after == defaultElo && FloorOf(before) != defaultElo)
                {
                    tally.ToDefault++;
                }
                else
                {
                    string key = after.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    tally.Floored[key] = tally.Floored.GetValueOrDefault(key) + 1;
                }

                set[$"{field}.{character.Name}.elo"] = EloRatings.JsNumber(after);
            }
        }

        // The mode's rating: the best reset character's, else its own reset; never below the best character's.
        if (!rating.TryGetValue($"elo_{mode}", out var modeElo) || !modeElo.IsNumeric)
        {
            return;
        }

        double modeBefore = modeElo.ToDouble();
        double modeAfter = Reset(modeBefore, Count(rating, $"wins_{mode}") + Count(rating, $"losses_{mode}"), minSets, defaultElo);
        if (!double.IsNaN(best) && best > modeAfter)
        {
            modeAfter = best;
        }

        if (double.IsNaN(modeAfter) || modeAfter == modeBefore)
        {
            return;
        }

        if (modeAfter == defaultElo && FloorOf(modeBefore) != defaultElo)
        {
            tally.ModeToDefault++;
        }
        else
        {
            tally.ModeFloored++;
        }

        set[$"elo_{mode}"] = EloRatings.JsNumber(modeAfter);
    }

    private static long Count(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out var v) && v.IsNumeric && !double.IsNaN(v.ToDouble()) ? (long)v.ToDouble() : 0;
}
