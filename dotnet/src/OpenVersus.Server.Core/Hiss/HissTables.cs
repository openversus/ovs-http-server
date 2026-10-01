using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Hiss;

/// <summary>
/// The game data tables the services read from the hiss the game downloads (hiss-amalgamation.json): each one's
/// {slug: {slug, data, ...}} map (its _hydra_compressed), parsed once. Only these sections are kept.
/// </summary>
internal static class HissTables
{
    private static readonly string[] s_sections =
        ["rift-config", "missions", "mission-objectives", "mission-containers", "mission-controlers", "mission-list"];

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

    /// <summary>A table's {slug: entry} map (empty when the hiss has none). Shared: never change it.</summary>
    internal static JsonObject Table(string section) => s_tables.Value[section] as JsonObject
        ?? throw new ArgumentException($"{section} is not a table kept here", nameof(section));

    /// <summary>An entry's data ({slug, data, ...}["data"]), or null for an unknown slug.</summary>
    internal static JsonObject? Data(string section, string? slug) => slug is null ? null : Table(section)[slug]?["data"] as JsonObject;
}
