using System.Text.Json.Nodes;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Hiss;

/// <summary>
/// The game data tables the services read from the hiss the game downloads (hiss-amalgamation.json): each one's
/// {slug: {slug, data, ...}} map (its _hydra_compressed), parsed once. Only these sections are kept.
/// The fighter tracks in milestone-reward-tracks are extended as FighterPass:ExtraTiers says, as the game is sent them.
/// </summary>
internal static class HissTables
{
    private const string Tracks = "milestone-reward-tracks";

    private static readonly string[] s_sections =
        ["rift-config", "missions", "mission-objectives", "mission-containers", "mission-controlers", "mission-list", Tracks];

    private static readonly Lazy<JsonObject> s_tables = new(() =>
    {
        using var stream = typeof(HissTables).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Hiss.hiss-amalgamation.json")
            ?? throw new InvalidOperationException("hiss-amalgamation.json is not embedded");
        var data = JsonNode.Parse(stream)!["body"]!["Data"]!.AsObject();
        var tables = new JsonObject();
        foreach (string section in s_sections)
        {
            tables[section] = data[section]?["_hydra_compressed"]?.DeepClone() ?? new JsonObject();
        }

        return tables;
    });

    private sealed record Extended(string Key, JsonObject Table);

    // The reward tracks with the Fighter Pass extension in force, made again when its settings change.
    private static volatile Extended? s_extended;

    /// <summary>A table's {slug: entry} map (empty when the hiss has none). Shared: never change it.</summary>
    internal static JsonObject Table(string section)
    {
        var table = GameTable(section);
        var settings = FighterPass.Current;
        string key = FighterPass.Key(settings);
        if (section != Tracks || key.Length == 0)
        {
            return table;
        }

        var extended = s_extended;
        if (extended is null || extended.Key != key)
        {
            extended = new Extended(key, FighterPass.Extend(table, settings));
            s_extended = extended;
        }

        return extended.Table;
    }

    /// <summary>An entry's data ({slug, data, ...}["data"]), or null for an unknown slug.</summary>
    internal static JsonObject? Data(string section, string? slug) => slug is null ? null : Table(section)[slug]?["data"] as JsonObject;

    /// <summary>An entry's data as the game's data has it, never extended.</summary>
    internal static JsonObject? GameData(string section, string? slug) => slug is null ? null : GameTable(section)[slug]?["data"] as JsonObject;

    private static JsonObject GameTable(string section) => s_tables.Value[section] as JsonObject
        ?? throw new ArgumentException($"{section} is not a table kept here", nameof(section));
}
