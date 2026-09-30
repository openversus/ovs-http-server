using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Static;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Rifts;

// PUT /ssc/invoke/start_rift_node {ChapterId, NodeId, RiftLobbyId, MultiplayParams}: the player picked a match node on
// the rift map, locked a loadout and is ready. On WB this allocated a Multiplay server; here it starts a match the way the
// TS custom lobby starts one with bots (IMatchLauncher), with the gameplay config a rift match has.
//
// The reference is the client's own offline backend (build f97148ff): its start_rift_node (0x1429ca0f0) answers an empty
// success and then hands the game an OnGameplayConfigNotified {MatchId (a new GUID), GameplayConfig} built by
// UMvsRiftGameplayConfigCreator (0x1429cbe50), whose fields differ from the websocket's PvP config in:
//   bIsRift true, bIsPvP false, bIsRanked/bIsCustomGame/bIsCasualSpecial false, RiftNodeId (the node GUID),
//   RiftNodeAttunement (MatchData.Attunement), HudSettings, CountdownDisplay, Map, WorldBuffs, bAllowMapHazards,
//   bIsTutorial (from MatchData), ScoreEvaluationRule TargetScoreIsLoss, ScoreAttributionRule AttributeToVictim,
//   MatchDurationSeconds = MatchDurationSecondsByDifficulty[min(difficulty, last)] or 240 when empty,
//   ModeString "1v1", or "2v2" when team 0 has more than one player,
//   TeamData [{TargetScore: team 0's stocks}, {TargetScore: team 1's stocks}] (the stocks each side can lose).
// Team 0's stocks: the rift's attrition pool (CurrentAttritionStocks) when the chapter's attrition settings at the
// lobby's difficulty carry player stocks over (bDoPlayerStocksAndDamageCarryOver), else FriendlyTeam.NumStocks. The
// client also caps the pool at the global rift settings' MaxStocksTakenIntoMatch, a value no data here holds (not in the
// hiss we send): uncapped. Team 1's: EnemyTeams[0].NumStocks, or 2 with no enemy team.
// Players: the human (TeamIndex 0), the friendly bots (TeamIndex 0) and the enemy bots (TeamIndex 1), each bot from the
// node's runtime data (BotLoadouts: AccountId "Bot0".., Character, Skin, StartingDamage) and its MatchData entry
// (Banner, ProfileIcon, RingOutVfx, BotBehaviorOverride, bUseCharacterDisplayName, Username), with its team's
// TeamBuffs as Buffs. BotDifficultyMin/Max stay 0, as the offline creator writes them.
//
// Redis, read     lobby:{RiftLobbyId} (the rift lobby: mode rift_lobby, riftConfigSlug, chapterDifficulty, playerIds),
//                 player:{player} (the locked character, skin), connections:{player} (current_ip)
// Mongo           the player's rift state (RiftStateService), for the attrition pool

public interface IRiftMatchService
{
    /// <summary>The start_rift_node answer for the player the session token (<paramref name="claims"/>) names; the match
    /// reaches the game over the websocket.</summary>
    Task<JsonObject> StartNodeAsync(JsonObject? claims, JsonObject? request, CancellationToken ct);
}

internal sealed class RiftMatchService(IServiceProvider services, IRiftStateService states, IMatchLauncher launcher,
    ILogger<RiftMatchService> log) : IRiftMatchService
{
    private static readonly Lazy<JsonArray> s_configs = new(() =>
        JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!["RiftConfigs"]!.AsArray());

    public async Task<JsonObject> StartNodeAsync(JsonObject? claims, JsonObject? request, CancellationToken ct)
    {
        string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        string lobbyId = Str(request, "RiftLobbyId") ?? "";
        string nodeId = Str(request, "NodeId") ?? "";
        string chapterId = Str(request, "ChapterId") ?? "";

        if (playerId is null || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("start_rift_node with no player id or no Redis; no match started");
            return Answer();
        }

        var lobby = (await redis.StringGetAsync($"lobby:{lobbyId}")) is { HasValue: true } raw ? JsonNode.Parse(raw.ToString()) as JsonObject : null;
        if (lobby is null || Str(lobby, "mode") != RiftLobbyService.Mode || lobby["playerIds"] is not JsonArray ids
            || !ids.Any(n => n is JsonValue p && p.TryGetValue(out string? s) && s == playerId))
        {
            log.LogWarning("start_rift_node from {Player}: {Lobby} is not a rift lobby of theirs; no match started", playerId, lobbyId);
            return Answer();
        }

        string slug = Str(lobby, "riftConfigSlug") ?? "";
        int difficulty = lobby["chapterDifficulty"] is JsonValue d && d.TryGetValue(out double n) ? (int)n : 0;
        var config = s_configs.Value.OfType<JsonObject>().FirstOrDefault(c => Str(c, "slug") == slug);
        if (config?["RiftMatchNodeData"]?[nodeId] is not JsonObject node || node["MatchData"] is not JsonObject matchData)
        {
            log.LogWarning("start_rift_node from {Player}: no match node {Node} in rift {Rift}; no match started", playerId, nodeId, slug);
            return Answer();
        }

        var player = (await redis.HashGetAllAsync($"player:{playerId}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var connection = (await redis.HashGetAllAsync($"connections:{playerId}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var human = new RiftHuman(playerId, Get(connection, "current_ip") ?? "",
            Or(Get(player, "character"), "character_shaggy"), Or(Get(player, "skin"), "skin_shaggy_default"));

        var runtimeNode = RiftLobbyService.RuntimeData(slug)["RuntimeNodeData"]?[nodeId] as JsonObject;
        var chapterAttrition = config["RiftChapterData"]?[chapterId]?["Attrition"] as JsonObject;
        int? poolStocks = CarriesPlayerStocksOver(chapterAttrition, difficulty)
            ? PoolStocks(await states.StateAsync(playerId, ct), slug, config["RiftData"]?["Attrition"] as JsonObject)
            : null;

        var launch = Build(human, nodeId, matchData, runtimeNode, difficulty, poolStocks);
        var started = await launcher.LaunchAsync(launch, ct);
        log.LogInformation("start_rift_node from {Player}: rift {Rift} chapter {Chapter} node {Node} difficulty {Difficulty} -> match {Match}",
            playerId, slug, chapterId, nodeId, difficulty, started?.MatchId ?? "(not started)");
        return Answer();
    }

    /// <summary>The player starting the node.</summary>
    internal sealed record RiftHuman(string PlayerId, string Ip, string Character, string Skin);

    /// <summary>The match for a rift node: see the comment at the top of this file.</summary>
    internal static MatchLaunch Build(RiftHuman human, string nodeId, JsonObject matchData, JsonObject? runtimeNode, int difficulty, int? poolStocks)
    {
        var players = new List<MatchPlayer> { new(human.PlayerId, 0, 0, true, human.Ip, false) };
        var overrides = new JsonObject
        {
            [human.PlayerId] = new JsonObject
            {
                ["Character"] = human.Character,
                ["Skin"] = human.Skin,
                ["Buffs"] = Slugs(matchData["FriendlyTeam"]?["TeamBuffs"]),
            },
        };

        AddBots(players, overrides, matchData["FriendlyTeam"] as JsonObject, runtimeNode?["FriendlyTeam"] as JsonObject, teamIndex: 0, firstIndexInTeam: 1);
        var enemies = matchData["EnemyTeams"] as JsonArray ?? [];
        var runtimeEnemies = runtimeNode?["EnemyTeams"] as JsonArray ?? [];
        for (int t = 0; t < enemies.Count; t++)
        {
            AddBots(players, overrides, enemies[t] as JsonObject, t < runtimeEnemies.Count ? runtimeEnemies[t] as JsonObject : null,
                teamIndex: t + 1, firstIndexInTeam: 0);
        }

        int friendlyStocks = poolStocks ?? Int(matchData["FriendlyTeam"]?["NumStocks"]) ?? 2;
        int enemyStocks = enemies.Count > 0 ? Int(enemies[0]?["NumStocks"]) ?? 2 : 2;
        var durations = matchData["MatchDurationSecondsByDifficulty"] as JsonArray ?? [];
        int duration = durations.Count > 0 ? Int(durations[Math.Clamp(difficulty, 0, durations.Count - 1)]) ?? 240 : 240;
        string mode = players.Count(p => p.TeamIndex == 0) > 1 ? "2v2" : "1v1";
        string map = Str(matchData, "Map") ?? "";

        var gameplayConfig = new JsonObject
        {
            ["bIsRift"] = true,
            ["bIsPvP"] = false,
            ["bIsRanked"] = false,
            ["bIsCustomGame"] = false,
            ["bIsCasualSpecial"] = false,
            ["bModeGrantsProgress"] = false,
            ["bIsTutorial"] = matchData["bIsTutorial"]?.DeepClone() ?? false,
            ["bAllowMapHazards"] = matchData["bAllowMapHazards"]?.DeepClone() ?? false,
            ["RiftNodeId"] = nodeId,
            ["RiftNodeAttunement"] = Or(Str(matchData, "Attunement"), "Attunements:None"),
            ["CountdownDisplay"] = Or(Str(matchData, "CountdownDisplay"), "CountdownTypes:XvY"),
            ["HudSettings"] = matchData["HudSettings"]?.DeepClone()
                ?? new JsonObject { ["bDisplayPortraits"] = true, ["bDisplayStocks"] = true, ["bDisplayTimer"] = true },
            ["WorldBuffs"] = Slugs(matchData["WorldBuffs"]),
            ["Map"] = map,
            ["ScoreEvaluationRule"] = "TargetScoreIsLoss",
            ["ScoreAttributionRule"] = "AttributeToVictim",
            ["MatchDurationSeconds"] = duration,
            ["ModeString"] = mode,
            ["TeamData"] = new JsonArray(new JsonObject { ["TargetScore"] = friendlyStocks }, new JsonObject { ["TargetScore"] = enemyStocks }),
        };

        return new MatchLaunch(mode, map, mode, players, gameplayConfig, overrides);
    }

    // The team's bots: runtime BotLoadouts[i] (who) with MatchData Bots[i] (how they look), after the humans' indexes.
    private static void AddBots(List<MatchPlayer> players, JsonObject overrides, JsonObject? team, JsonObject? runtimeTeam, int teamIndex, int firstIndexInTeam)
    {
        var loadouts = runtimeTeam?["BotLoadouts"] as JsonArray ?? [];
        var bots = team?["Bots"] as JsonArray ?? [];
        for (int i = 0; i < loadouts.Count; i++)
        {
            if (loadouts[i] is not JsonObject loadout || Str(loadout, "AccountId") is not { Length: > 0 } botId)
            {
                continue;
            }

            var look = i < bots.Count ? bots[i] as JsonObject : null;
            int indexInTeam = firstIndexInTeam + players.Count(p => p.TeamIndex == teamIndex && p.IsBot);
            players.Add(new MatchPlayer(botId, indexInTeam * 2 + teamIndex, teamIndex, false, "", true));
            overrides[botId] = new JsonObject
            {
                ["Character"] = Str(loadout, "Character") ?? "",
                ["Skin"] = Str(loadout, "Skin") ?? "",
                ["StartingDamage"] = loadout["StartingDamage"]?.DeepClone() ?? 0,
                ["Banner"] = Or(Str(look, "Banner"), "banner_default"),
                ["ProfileIcon"] = Or(Str(look, "ProfileIcon"), "profile_icon_default_gold"),
                ["RingoutVfx"] = Or(Str(look, "RingOutVfx"), "ring_out_vfx_default"),
                ["BotBehaviorOverride"] = Str(look, "BotBehaviorOverride") ?? "",
                ["bUseCharacterDisplayName"] = look?["bUseCharacterDisplayName"]?.DeepClone() ?? true,
                ["Username"] = look?["Username"]?.DeepClone() ?? new JsonObject(),
                ["Buffs"] = Slugs(team?["TeamBuffs"]),
                ["Perks"] = new JsonArray(),
                ["BotDifficultyMin"] = 0,
                ["BotDifficultyMax"] = 0,
            };
        }
    }

    /// <summary>Whether the chapter's attrition settings at <paramref name="difficulty"/> (the last entry beyond them)
    /// carry the player's stocks and damage from match to match.</summary>
    internal static bool CarriesPlayerStocksOver(JsonObject? chapterAttrition, int difficulty)
    {
        if (chapterAttrition?["DifficultyModifiedSettings"] is not JsonArray settings || settings.Count == 0)
        {
            return false;
        }

        return settings[Math.Clamp(difficulty, 0, settings.Count - 1)]?["bDoPlayerStocksAndDamageCarryOver"] is JsonValue b
            && b.TryGetValue(out bool carry) && carry;
    }

    // The pool's CurrentAttritionStocks: the rift's own pool when its config says so, else the global one.
    private static int? PoolStocks(JsonObject state, string slug, JsonObject? riftAttrition)
    {
        bool riftPool = riftAttrition?["bTargetRiftPoolInsteadOfGlobalPool"] is JsonValue b && b.TryGetValue(out bool own) && own;
        return Int(state["PlayerAttrition"]?[riftPool ? slug : "GlobalAttrition"]?["CurrentAttritionStocks"]);
    }

    private static JsonObject Answer() => new() { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 };

    private static JsonArray Slugs(JsonNode? list) => list is JsonArray a ? a.DeepClone().AsArray() : [];

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double n) ? (int)n : null;

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
