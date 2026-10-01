using System.Text.Json.Nodes;

using OpenVersus.Server.Core.Hiss;

namespace OpenVersus.Server.Core.Rifts;

// A rift node's missions (the stars beside it on the map), judged from the counters the game reports with the match
// result (EndOfMatchStats.PlayerMissionUpdates[player], e.g. "Stat:Game:Character:TotalRingouts": 2).
//
// All from the hiss the game has (hiss-amalgamation.json), not load_rifts: the game shows each node's missions from
// the hiss rift-config, whose lists differ from the TS load_rifts copy (the Joker node's third star is
// mis_rift_total_fully_charged_attacks_hit_once there, mis_save_ally_from_bubble in load_rifts).
//   rift-config[rift].data.RiftMatchNodeData[node].Missions  [{Mission, ChapterDifficultyThreshold, Reward?}]: a
//                  node's missions at a difficulty are those whose threshold is that difficulty (five at Easy, as shown)
//   missions[slug].data.MvsMissionData  {MissionObjectives [{ObjectivePtr, Count}], bAllObjectivesMustBeCompleted,
//                  ProgressConstraints [objective slugs, each must hold]}
//   mission-objectives[slug].data.ObjectiveFlags  [{GameplayTag, Operator, Value}], all must hold
// and the tags of skins and characters from rift-item-tags.json (the TS server's INVENTORY_DEFINITIONS ItemRewardTags,
// written by tools/rifts/gen_item_tags.mjs; the Mongo data assets hold none, and the hiss's CharacterMetaData only some
// characters' tags, not Wonder Woman's, say).
// A mission is judged by what its objectives test, never by its description: the two often disagree in the game's
// own data, and the objectives are what WB's server went by.
//
// A flag holds when:
//   Objective:Match:Win == true        the player's team won
//   Objective:Match:Tag:Skin == <tag>  the player's skin carries the tag (e.g. TS:Fixed:Universe:DC; a skin with no
//                                      tags known falls back to its character's)
//   <counter> +=                       the match's counter reached the objective's Count
//   <counter> >= / > / == / <= / < v   the match's counter compared with Value
// Anything else (another operator, a tag this cannot know) does not hold, and is reported.

/// <summary>What a rift match's player did, for judging missions.</summary>
internal sealed record RiftMissionContext(bool Won, string Character, string Skin, JsonObject? Counters);

internal static class RiftMissions
{
    private static readonly Lazy<JsonObject> s_itemTags = new(() =>
    {
        using var stream = typeof(RiftMissions).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Rifts.rift-item-tags.json")
            ?? throw new InvalidOperationException("rift-item-tags.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>The rift's configuration (RiftData, RiftChapterData, RiftMatchNodeData, ...) as the hiss rift-config holds
    /// it, which is what the game shows; null for an unknown rift.</summary>
    internal static JsonObject? RiftConfig(string slug) => HissTables.Table("rift-config")[slug]?["data"] as JsonObject;

    /// <summary>Every rift of the hiss rift-config: slug to {slug, data}.</summary>
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> RiftConfigs() => HissTables.Table("rift-config");

    /// <summary>The node's mission slugs at <paramref name="difficulty"/>, in the order the game lists them.</summary>
    internal static IReadOnlyList<string> NodeMissions(string slug, string nodeId, int difficulty) =>
        (HissTables.Table("rift-config")[slug]?["data"]?["RiftMatchNodeData"]?[nodeId]?["Missions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(m => Num(m["ChapterDifficultyThreshold"]) == difficulty)
            .Select(m => Str(m["Mission"]))
            .OfType<string>()
            .ToList();

    /// <summary>The missions of <paramref name="missions"/> the match satisfied; <paramref name="unknown"/> collects
    /// flags this could not judge.</summary>
    internal static List<string> Earned(IEnumerable<string> missions, RiftMissionContext match, ICollection<string> unknown)
    {
        var earned = new List<string>();
        foreach (string slug in missions)
        {
            if (HissTables.Table("missions")[slug]?["data"]?["MvsMissionData"] is not JsonObject mission)
            {
                unknown.Add($"mission {slug} (not in the hiss)");
                continue;
            }

            bool constraints = (mission["ProgressConstraints"] as JsonArray ?? []).All(c => Objective(Str(c), 1, match, unknown));
            var objectives = (mission["MissionObjectives"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(o => Objective(Str(o["ObjectivePtr"]), Num(o["Count"]) ?? 1, match, unknown))
                .ToList();
            bool all = mission["bAllObjectivesMustBeCompleted"] is JsonValue a && a.TryGetValue(out bool b) && b;
            if (constraints && objectives.Count > 0 && (all ? objectives.All(x => x) : objectives.Any(x => x)))
            {
                earned.Add(slug);
            }
        }

        return earned;
    }

    private static bool Objective(string? slug, double count, RiftMissionContext match, ICollection<string> unknown)
    {
        if (slug is null || HissTables.Table("mission-objectives")[slug]?["data"]?["ObjectiveFlags"] is not JsonArray flags || flags.Count == 0)
        {
            unknown.Add($"objective {slug ?? "(none)"} (not in the hiss)");
            return false;
        }

        return flags.OfType<JsonObject>().All(f => Flag(f, count, match, unknown));
    }

    private static bool Flag(JsonObject flag, double count, RiftMissionContext match, ICollection<string> unknown)
    {
        string tag = Str(flag["GameplayTag"]) ?? "";
        string op = Str(flag["Operator"]) ?? "";
        switch (tag)
        {
            case "Objective:Match:Win" when op == "==":
                return match.Won == (flag["Value"] is JsonValue v && v.TryGetValue(out bool want) ? want : true);
            case "Objective:Match:Tag:Skin" when op == "==":
                string wantedTag = Str(flag["TagValue"]) ?? Str(flag["Value"]) ?? "";
                return SkinTags(match.Skin, match.Character).Contains(wantedTag);
        }

        double have = Counter(match.Counters?[tag]);
        double? value = Num(flag["Value"]);
        switch (op)
        {
            case "+=":
                return have >= count;
            case ">=" when value is { } ge:
                return have >= ge;
            case ">" when value is { } gt:
                return have > gt;
            case "==" when value is { } eq:
                return have == eq;
            case "<=" when value is { } le:
                return have <= le;
            case "<" when value is { } lt:
                return have < lt;
            default:
                unknown.Add($"flag {tag} {op} {flag["Value"]?.ToJsonString() ?? ""}");
                return false;
        }
    }

    /// <summary>A skin's gameplay tags, else its character's; none when neither is known.</summary>
    internal static HashSet<string> SkinTags(string skin, string character) =>
        (s_itemTags.Value[skin] as JsonArray ?? s_itemTags.Value[character] as JsonArray ?? []).Select(Str).OfType<string>().ToHashSet();

    // A reported counter: a number, or {"_hydra_double": n} as a Hydra double reaches JSON; absent is 0.
    private static double Counter(JsonNode? node) => node is JsonObject o ? Number(o["_hydra_double"]) ?? 0 : Number(node) ?? 0;

    /// <summary>A JSON number as a double, however the node holds it (parsed text, or an int or double set in code:
    /// JsonValue.TryGetValue does not convert between those).</summary>
    internal static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static double? Num(JsonNode? node) => Number(node);

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
