using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Rifts;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Missions;

// Mission progress from a match result. The TS server handles submit_end_of_match_stats (each player's game sends its
// own) and publishes {matchId, playerId, winningTeamIndex, missionUpdates (that player's counters, e.g.
// "Stat:Game:Character:TotalRingouts": 2)} on match:end_of_match_stats (docs/MIGRATION-BRIDGES.md 4, as rift
// progress). The match as the game saw it comes from the match notification at {matchId} (players with teamIndex,
// mode, map, isCustomGame, gameplayConfigOverride: the TS websocket's config fields, which a C# match overrides) and
// the player's character and skin from rift_match:{matchId} (a rift match) or player:{id} (what the TS websocket built
// the match config from).
//
// Each unclaimed mission of the live containers moves when the match meets its controller's Constraints and
// UnlockConstraints and its own ProgressConstraints; each of its objectives then gains, when all its non-adding flags
// hold, the sum of its += counters (whole numbers: a fractional counter is rounded down), or 1 when it has none (a
// match that meets the conditions), capped at the mission's Count for it. The flags (hiss mission-objectives):
//   Stat:..., Fighter:..., Objective:Match:... += / >= / > / == / <= / <   a counter the game reported (absent: 0)
//   Objective:Match:Tag:Skin == <tag>        the skin played carries the tag, else its character (RiftMissions.SkinTags)
//   Objective:Match:Mode:Type:Play == <mode> the match's mode (1v1, 2v2, ffa)
//   Objective:Match:Map:Play == <tag>        TS:Fixed:Maps:<the match's map, any _V2-style suffix dropped>
//   Objective:Match:Win == <bool>            the player's team won
//   Constraint:Match:IsPvP / IsPvE == <bool> the match config's bIsPvP (PvE: not PvP)
//   Constraint:Inventory:OwnsItem == <item>  held: every player owns every character here
// Anything else does not hold, and is logged once per match. Custom games move nothing unless
// Missions:CustomGamesProgress. Claimability is the client's to work out (docs/MISSIONS.md).
//
// Redis, read      {matchId}, rift_match:{matchId}, player:{id}
// Redis, written   mission_match:{matchId}:{player} NX EX 1 h (one record per player and match)
// Mongo, written   missionobjects (MissionService), version-guarded
// Published        ws:send {playerIds, message}: MissionUpdatesComplete {template_id, data: the mission object}

/// <summary>What a player's match was, for judging missions.</summary>
internal sealed record MissionMatch(bool Won, string Character, string Skin, string Mode, string Map, bool IsPvP, JsonObject? Counters);

internal static class MissionRules
{
    /// <summary>What <paramref name="objective"/> gains from the match, or null when its conditions do not hold.</summary>
    internal static int? Gain(string? objective, MissionMatch match, ICollection<string> unknown)
    {
        if (HissTables.Data("mission-objectives", objective)?["ObjectiveFlags"] is not JsonArray flags || flags.Count == 0)
        {
            unknown.Add($"objective {objective ?? "(none)"} (not in the hiss)");
            return null;
        }

        double gain = 0;
        bool adds = false;
        foreach (var flag in flags.OfType<JsonObject>())
        {
            if (Str(flag["Operator"]) == "+=")
            {
                adds = true;
                gain += Math.Max(0, Math.Floor(Counter(match.Counters?[Str(flag["GameplayTag"]) ?? ""])));
            }
            else if (!Holds(flag, match, unknown))
            {
                return null;
            }
        }

        return adds ? (int)Math.Min(gain, int.MaxValue) : 1;
    }

    /// <summary>Whether every flag of <paramref name="objective"/> holds as a condition (a constraint objective).</summary>
    internal static bool Holds(string? objective, MissionMatch match, ICollection<string> unknown) =>
        HissTables.Data("mission-objectives", objective)?["ObjectiveFlags"] is JsonArray flags && flags.Count > 0
            ? flags.OfType<JsonObject>().All(f => Str(f["Operator"]) == "+=" ? Counter(match.Counters?[Str(f["GameplayTag"]) ?? ""]) > 0 : Holds(f, match, unknown))
            : Unknown(unknown, $"objective {objective ?? "(none)"} (not in the hiss)");

    private static bool Holds(JsonObject flag, MissionMatch match, ICollection<string> unknown)
    {
        string tag = Str(flag["GameplayTag"]) ?? "";
        string op = Str(flag["Operator"]) ?? "";
        string? text = Str(flag["TagValue"]) ?? Str(flag["Value"]);
        bool? flagBool = flag["Value"] is JsonValue b && b.TryGetValue(out bool vb) ? vb : null;
        switch (tag)
        {
            case "Objective:Match:Tag:Skin" when op == "==":
                return text is not null && RiftMissions.SkinTags(match.Skin, match.Character).Contains(text);
            case "Objective:Match:Mode:Type:Play" when op == "==":
                return string.Equals(text, match.Mode, StringComparison.OrdinalIgnoreCase);
            case "Objective:Match:Map:Play" when op == "==":
                return text is not null && text == "TS:Fixed:Maps:" + match.Map.Split('_')[0];
            case "Objective:Match:Win" when op == "==":
                return match.Won == (flagBool ?? true);
            case "Constraint:Match:IsPvP" when op == "==":
                return match.IsPvP == (flagBool ?? true);
            case "Constraint:Match:IsPvE" when op == "==":
                return !match.IsPvP == (flagBool ?? true);
            case "Constraint:Inventory:OwnsItem" when op == "==":
                return true;
        }

        double have = Counter(match.Counters?[tag]);
        double? value = RiftMissions.Number(flag["Value"]);
        return op switch
        {
            ">=" when value is { } v => have >= v,
            ">" when value is { } v => have > v,
            "==" when value is { } v => have == v,
            "<=" when value is { } v => have <= v,
            "<" when value is { } v => have < v,
            _ => Unknown(unknown, $"flag {tag} {op} {flag["Value"]?.ToJsonString() ?? ""}"),
        };
    }

    /// <summary>Moves the unclaimed missions of the live containers in <paramref name="serverData"/>; true when any
    /// progress changed.</summary>
    internal static bool Apply(JsonObject serverData, IReadOnlyList<string> live, MissionMatch match, ICollection<string> unknown)
    {
        bool changed = false;
        var containers = serverData["MissionControllerContainers"] as JsonObject ?? [];
        foreach (string slug in live)
        {
            if (containers[slug]?["MissionControllers"] is not JsonObject controllers)
            {
                continue;
            }

            foreach (var (controllerSlug, controllerState) in controllers)
            {
                var controller = HissTables.Data("mission-controlers", controllerSlug)?["MvsMissionController"] as JsonObject;
                var gates = (controller?["Constraints"] as JsonArray ?? []).Concat(controller?["UnlockConstraints"] as JsonArray ?? []);
                if (controllerState?["Missions"] is not JsonArray groups || !gates.All(g => Holds(Str(g), match, unknown)))
                {
                    continue;
                }

                foreach (var (missionSlug, missionState) in groups.OfType<JsonObject>().SelectMany(g => g))
                {
                    if (HissTables.Data("missions", missionSlug)?["MvsMissionData"] is not JsonObject mission
                        || missionState?["MissionObjectives"] is not JsonArray progress
                        || !(mission["ProgressConstraints"] as JsonArray ?? []).All(c => Holds(Str(c), match, unknown)))
                    {
                        continue;
                    }

                    var counts = (mission["MissionObjectives"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                    foreach (var objective in progress.OfType<JsonObject>())
                    {
                        string? objectiveSlug = Str(objective["Slug"]);
                        int count = (int)(RiftMissions.Number(counts.FirstOrDefault(c => Str(c["ObjectivePtr"]) == objectiveSlug)?["Count"]) ?? 1);
                        int have = (int)(RiftMissions.Number(objective["Progress"]) ?? 0);
                        if (have >= count || Gain(objectiveSlug, match, unknown) is not { } gain || gain == 0)
                        {
                            continue;
                        }

                        objective["Progress"] = (int)Math.Min((long)have + gain, count);
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private static bool Unknown(ICollection<string> unknown, string what)
    {
        unknown.Add(what);
        return false;
    }

    // A reported counter: a number, or {"_hydra_double": n} as a Hydra double reaches JSON; absent is 0.
    private static double Counter(JsonNode? node) => node is JsonObject o ? RiftMissions.Number(o["_hydra_double"]) ?? 0 : RiftMissions.Number(node) ?? 0;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>Records match results into missions (Missions:Enabled).</summary>
internal sealed class MissionResultSubscriber(IServiceProvider services, IOptionsMonitor<MissionSettings> settings, IMissionService missions,
    ILogger<MissionResultSubscriber> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } redis)
        {
            return;
        }

        log.LogWarning("MIGRATION BRIDGE: mission progress is recorded from the TS server's {Channel} and sent through its websocket (ws:send); see dotnet/docs/MIGRATION-BRIDGES.md (4)", RiftResultSubscriber.Channel);
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(RiftResultSubscriber.Channel), (channel, message) => _ = HandleAsync(message.ToString()));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleAsync(string message)
    {
        try
        {
            if (!settings.CurrentValue.Enabled || JsonNode.Parse(message) is not JsonObject result
                || result["matchId"] is not JsonValue m || !m.TryGetValue(out string? matchId)
                || result["playerId"] is not JsonValue p || !p.TryGetValue(out string? playerId))
            {
                return;
            }

            int? winning = result["winningTeamIndex"] is JsonValue w && w.TryGetValue(out double n) ? (int)n : null;
            await missions.RecordMatchAsync(matchId, playerId, winning, result["missionUpdates"] as JsonObject, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Nothing else would ever see it: this runs on the subscription's callback, not a request.
            log.LogError(e, "Mission progress from {Message} not recorded: {Error}", message, e.Message);
        }
    }
}
