using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Rifts;

// The rifts this server offers: the TS load_rifts answer's rifts (Static/ssc-load-rifts.json), plus the rifts the hiss
// rift-config has and it lacks (the four Season 6 rogue rifts), with two policies applied (docs/RIFTS.md, "Ended rifts"):
//   - no rift ends: a rift with bRiftHasEndTime keeps it, its RiftEndTime moved EndTimeYears later (in load_rifts and
//     in the hiss the game downloads, HissService), since every rogue rift's date passed in 2025;
//   - a rift load_rifts lacks gets the runtime data a new player's copy of a rift has, generated from its config the
//     way the other rifts' copies are shaped (the newest, Season 5 rogue rifts', shape):
//       DynamicInstanceRuntimeData[rift]
//         RuntimeChapterData[chapter] {bIsChapterComplete false, NodeCompletionsByDifficulty {}, CurrentDifficulty -1,
//                                       HighestDifficultyCompleted 0}
//         RuntimeNodeData[match node] {EnemyTeams [{BotLoadouts, Stocks, LastKnownConfiguredStocks} per enemy team],
//                                      FriendlyTeam {BotLoadouts}}; each bot {AccountId "Bot<n>" (enemies first),
//                                      Character, Skin, StartingDamage 0}, Stocks the team's NumStocks
//       PlayerInstanceRuntimeData[rift]
//         RuntimeChapterData[chapter] {CauldronsByDifficulty: one {CurrentScore 0, ClaimedTiers [false per tier]} per
//                                       entry of the chapter's configured cauldrons}
//         RuntimeNodeData[match node] {CompletedMissions {}, ClaimedOneTimeRewardGuidsByDifficulty {}, ClaimedBattlepassXp {}}
//     A bot's character and skin: what WB's server picked for its CharacterSet on the rifts load_rifts has (nothing
//     here defines the sets); 32 of the 35 sets Season 6 uses always gave one pick, and a set seen with several picks
//     gets one of them chosen by the node and slot, the same every time.

public static class RiftCatalog
{
    /// <summary>How much later every rift's end time is set (its day, month and time kept): Rifts:EndTimeYears.</summary>
    public static int EndTimeYears { get; private set; } = 20;

    /// <summary>Whether the hiss-only rifts are offered: Rifts:OfferHissOnlyRifts.</summary>
    public static bool OfferHissOnlyRifts { get; private set; } = true;

    /// <summary>Takes the settings; before the catalog is first used (at startup), as it is built once.</summary>
    public static void Configure(RiftSettings settings)
    {
        EndTimeYears = settings.EndTimeYears;
        OfferHissOnlyRifts = settings.OfferHissOnlyRifts;
    }

    private static readonly Lazy<Catalog> s_catalog = new(Build);

    /// <summary>The load_rifts RiftConfigs: the TS answer's and the hiss-only rifts, end times moved.</summary>
    public static JsonArray Configs => s_catalog.Value.Configs;

    /// <summary>A new player's DynamicInstanceRuntimeData before its progress is cleared (the frozen copy and the
    /// generated entries). Shared: callers clone.</summary>
    public static JsonObject Dynamic => s_catalog.Value.Dynamic;

    /// <summary>A new player's PlayerInstanceRuntimeData (the frozen copy and the generated entries). Shared: callers clone.</summary>
    public static JsonObject Player => s_catalog.Value.Player;

    private sealed record Catalog(JsonArray Configs, JsonObject Dynamic, JsonObject Player);

    private static Catalog Build()
    {
        var frozen = JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!.AsObject();
        var configs = frozen["RiftConfigs"]!.AsArray();
        var dynamic = frozen["DynamicInstanceRuntimeData"]!.AsObject();
        var player = frozen["PlayerInstanceRuntimeData"]!.AsObject();
        var picks = ObservedPicks(configs, dynamic);

        var known = configs.OfType<JsonObject>().Select(c => (string?)c["slug"]).ToHashSet();
        foreach (var (slug, entry) in RiftMissions.RiftConfigs())
        {
            if (!OfferHissOnlyRifts || known.Contains(slug) || entry?["data"] is not JsonObject data)
            {
                continue;
            }

            configs.Add(new JsonObject
            {
                ["RiftData"] = data["RiftData"]?.DeepClone(),
                ["RiftChapterData"] = data["RiftChapterData"]?.DeepClone(),
                ["RiftStartNodeData"] = data["RiftStartNodeData"]?.DeepClone(),
                ["RiftMatchNodeData"] = data["RiftMatchNodeData"]?.DeepClone(),
                ["RiftEndNodeData"] = data["RiftEndNodeData"]?.DeepClone(),
                ["slug"] = slug,
                ["RiftType"] = data["RiftType"]?.DeepClone(),
            });
            var (newDynamic, newPlayer) = NewRuntime(data, picks);
            dynamic[slug] = newDynamic;
            player[slug] = newPlayer;
        }

        foreach (var config in configs.OfType<JsonObject>())
        {
            ExtendEndTime(config["RiftData"] as JsonObject);
        }

        return new Catalog(configs, dynamic, player);
    }

    /// <summary>Moves every rift's end time in a hiss rift-config section (rift slug to {slug, data}).</summary>
    internal static void ExtendEndTimes(JsonObject? riftConfig)
    {
        foreach (var (_, entry) in riftConfig ?? [])
        {
            ExtendEndTime(entry?["data"]?["RiftData"] as JsonObject);
        }
    }

    private static void ExtendEndTime(JsonObject? riftData)
    {
        if (riftData?["bRiftHasEndTime"] is JsonValue has && has.TryGetValue(out bool ends) && ends
            && EndTimeYears != 0 && riftData["RiftEndTime"] is JsonObject end && RiftMissions.Number(end["Year"]) is { } year)
        {
            end["Year"] = (int)year + EndTimeYears;
        }
    }

    /// <summary>A rift's new-player runtime data (dynamic, player), generated from its config.</summary>
    internal static (JsonObject Dynamic, JsonObject Player) NewRuntime(JsonObject data, IReadOnlyDictionary<string, List<(string Character, string Skin)>> picks)
    {
        var dynamicChapters = new JsonObject();
        var playerChapters = new JsonObject();
        foreach (var (chapterId, chapter) in data["RiftChapterData"] as JsonObject ?? [])
        {
            dynamicChapters[chapterId] = new JsonObject
            {
                ["bIsChapterComplete"] = false,
                ["NodeCompletionsByDifficulty"] = new JsonObject(),
                ["CurrentDifficulty"] = -1,
                ["HighestDifficultyCompleted"] = 0,
            };
            playerChapters[chapterId] = new JsonObject
            {
                ["CauldronsByDifficulty"] = new JsonArray([.. (chapter?["CauldronsByDifficulty"] as JsonArray ?? []).Select(c => (JsonNode)new JsonObject
                {
                    ["CurrentScore"] = 0,
                    ["ClaimedTiers"] = new JsonArray([.. (c?["Tiers"] as JsonArray ?? []).Select(_ => (JsonNode)false)]),
                })]),
            };
        }

        var dynamicNodes = new JsonObject();
        var playerNodes = new JsonObject();
        foreach (var (nodeId, node) in data["RiftMatchNodeData"] as JsonObject ?? [])
        {
            int next = 0;
            var enemies = new JsonArray();
            foreach (var team in node?["MatchData"]?["EnemyTeams"] as JsonArray ?? [])
            {
                int stocks = (int)(RiftMissions.Number(team?["NumStocks"]) ?? 0);
                enemies.Add(new JsonObject
                {
                    ["BotLoadouts"] = Loadouts(team, nodeId, picks, ref next),
                    ["Stocks"] = stocks,
                    ["LastKnownConfiguredStocks"] = stocks,
                });
            }

            dynamicNodes[nodeId] = new JsonObject
            {
                ["EnemyTeams"] = enemies,
                ["FriendlyTeam"] = new JsonObject { ["BotLoadouts"] = Loadouts(node?["MatchData"]?["FriendlyTeam"], nodeId, picks, ref next) },
            };
            playerNodes[nodeId] = new JsonObject
            {
                ["CompletedMissions"] = new JsonObject(),
                ["ClaimedOneTimeRewardGuidsByDifficulty"] = new JsonObject(),
                ["ClaimedBattlepassXp"] = new JsonObject(),
            };
        }

        return (new JsonObject { ["RuntimeChapterData"] = dynamicChapters, ["RuntimeNodeData"] = dynamicNodes },
                new JsonObject { ["RuntimeChapterData"] = playerChapters, ["RuntimeNodeData"] = playerNodes });
    }

    private static JsonArray Loadouts(JsonNode? team, string nodeId, IReadOnlyDictionary<string, List<(string Character, string Skin)>> picks, ref int next)
    {
        var loadouts = new JsonArray();
        foreach (var bot in team?["Bots"] as JsonArray ?? [])
        {
            string set = bot?["CharacterSet"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
            if (!picks.TryGetValue(set, out var options) || options.Count == 0)
            {
                continue;
            }

            var (character, skin) = options[(int)((uint)StableHash($"{nodeId}:{next}") % (uint)options.Count)];
            loadouts.Add(new JsonObject { ["AccountId"] = $"Bot{next}", ["Character"] = character, ["Skin"] = skin, ["StartingDamage"] = 0 });
            next++;
        }

        return loadouts;
    }

    /// <summary>What WB's server picked for each CharacterSet, from the rifts whose runtime data the frozen copy holds
    /// (the node's bots by team and slot, against the runtime BotLoadouts), each pick once, in first-seen order.</summary>
    internal static Dictionary<string, List<(string Character, string Skin)>> ObservedPicks(JsonArray configs, JsonObject dynamic)
    {
        var picks = new Dictionary<string, List<(string, string)>>();
        foreach (var config in configs.OfType<JsonObject>())
        {
            var runtime = dynamic[(string?)config["slug"] ?? ""]?["RuntimeNodeData"] as JsonObject;
            foreach (var (nodeId, node) in config["RiftMatchNodeData"] as JsonObject ?? [])
            {
                if (runtime?[nodeId] is not JsonObject nodeRuntime)
                {
                    continue;
                }

                var teams = (node?["MatchData"]?["EnemyTeams"] as JsonArray ?? [])
                    .Select((t, i) => (t, nodeRuntime["EnemyTeams"]?[i]))
                    .Append((node?["MatchData"]?["FriendlyTeam"], nodeRuntime["FriendlyTeam"]));
                foreach (var (team, teamRuntime) in teams)
                {
                    var bots = team?["Bots"] as JsonArray ?? [];
                    var loadouts = teamRuntime?["BotLoadouts"] as JsonArray ?? [];
                    for (int i = 0; i < Math.Min(bots.Count, loadouts.Count); i++)
                    {
                        if (bots[i]?["CharacterSet"] is JsonValue sv && sv.TryGetValue(out string? set)
                            && loadouts[i]?["Character"] is JsonValue cv && cv.TryGetValue(out string? character)
                            && loadouts[i]?["Skin"] is JsonValue kv && kv.TryGetValue(out string? skin))
                        {
                            var list = picks.TryGetValue(set, out var existing) ? existing : picks[set] = [];
                            if (!list.Contains((character, skin)))
                            {
                                list.Add((character, skin));
                            }
                        }
                    }
                }
            }
        }

        return picks;
    }

    // FNV-1a: the same pick for a node and slot on every run and every replica (string.GetHashCode is per process).
    private static int StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)hash;
    }
}
