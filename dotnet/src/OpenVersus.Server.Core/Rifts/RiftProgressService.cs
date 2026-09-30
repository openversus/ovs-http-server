using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Static;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Rifts;

// Each player's rift runtime data, and what a rift match's result changes in it.
//
// The runtime data is what load_rifts sends as DynamicInstanceRuntimeData (per rift: RuntimeChapterData, each chapter's
// progress; RuntimeNodeData, each node's generated bots) and PlayerInstanceRuntimeData (cauldrons, missions, claimed
// rewards). A player's first copy is the TS server's frozen answer with every chapter's progress cleared (it held one
// account's two finished tutorial nodes): everyone starts from scratch, keeping the frozen copy's bots.
//
// After a match WB's server worked the progress out itself and pushed it; the client never asks (it sends no
// complete_rift_node). The TS server handles submit_end_of_match_stats and publishes each result on
// match:end_of_match_stats (docs/MIGRATION-BRIDGES.md); a rift match is known by rift_match:{match}, written when
// start_rift_node starts it. A win adds the node to its chapter's NodeCompletionsByDifficulty["<difficulty>"] (the
// format of the game's WB-era cache) and sets CurrentDifficulty; win or lose, the rift state's LastPlayed becomes the
// node. The game is then told with the notifications its router takes for these (0x140d08390, build f97148ff):
// OnLobbyRuntimeDataUpdated {LobbyId, RuntimeData (the rift's entry, as the lobby's RuntimeData)} and
// OnLobbyRiftStateUpdated {LobbyId, RiftState}, sent through the TS websocket (ws:send).
// A win also earns the node's missions (stars) the match satisfied (RiftMissions): each new one is added to the node's
// CompletedMissions["<difficulty>"] in PlayerInstanceRuntimeData and counted in the chapter's cauldron score at that
// difficulty, and the game is sent OnPlayerInstanceUpdated {LobbyId, PlayerInstance (the rift's entry), RiftSlug} (its
// handler, 0x142a27740, reads RiftSlug and then PlayerInstance). Earned
// missions are kept only on a win; the ones not earned stay open for another attempt.
// Not done yet: a loss's consequences (enemy stocks carried over, attrition), mission rewards (lootboxes), bIsChapterComplete and
// HighestDifficultyCompleted (most likely set when the player clicks the chapter's end node, finish_rift_chapter).
//
// Mongo, read/written  riftinstances {account_id, dynamic, player, createdAt, updatedAt}; the rift state (RiftStateService)
// Redis, read          rift_match:{match} (JSON: playerId, lobbyId, slug, chapterId, nodeId, difficulty, character, skin)
// Redis, written       rift_match:{match}:recorded NX EX 1 h (one record per match, however many services hear it)
// Published            ws:send ({playerIds, message}: the TS websocket sends message to them)

public interface IRiftProgressService
{
    /// <summary>The player's runtime data: DynamicInstanceRuntimeData and PlayerInstanceRuntimeData; created on first use.</summary>
    Task<(JsonObject Dynamic, JsonObject Player)> InstanceAsync(string playerId, CancellationToken ct);

    /// <summary>Records a match result if the match is a rift match not recorded yet.</summary>
    Task RecordResultAsync(string matchId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct);
}

internal sealed class RiftProgressService(IServiceProvider services, IRiftStateService states, TimeProvider time,
    ILogger<RiftProgressService> log) : IRiftProgressService
{
    public const string Collection = "riftinstances";
    public const string WsSendChannel = "ws:send";

    /// <summary>The key start_rift_node writes for a rift match.</summary>
    public static string MatchKey(string matchId) => $"rift_match:{matchId}";

    public async Task<(JsonObject Dynamic, JsonObject Player)> InstanceAsync(string playerId, CancellationToken ct)
    {
        var instances = Instances();
        var stored = await instances.Find(new BsonDocument("account_id", playerId)).FirstOrDefaultAsync(ct);
        if (stored?["dynamic"] is BsonDocument dynamic && stored["player"] is BsonDocument player)
        {
            // A rift added since (RiftCatalog) joins as a new player's copy; it is stored with the next change.
            var (d, p) = (Lean.Value(dynamic)!.AsObject(), Lean.Value(player)!.AsObject());
            AddMissingRifts(d, NewDynamic());
            AddMissingRifts(p, NewPlayer());
            return (d, p);
        }

        var (newDynamic, newPlayer) = (NewDynamic(), NewPlayer());
        var now = time.GetUtcNow().UtcDateTime;
        await instances.InsertOneAsync(new BsonDocument
        {
            ["account_id"] = playerId,
            ["dynamic"] = BsonDocument.Parse(Js.Stringify(newDynamic)),
            ["player"] = BsonDocument.Parse(Js.Stringify(newPlayer)),
            ["createdAt"] = now,
            ["updatedAt"] = now,
        }, cancellationToken: ct);
        log.LogInformation("Created the rift runtime data of {Player}", playerId);
        return (newDynamic, newPlayer);
    }

    public async Task RecordResultAsync(string matchId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis
            || (await redis.StringGetAsync(MatchKey(matchId))) is not { HasValue: true } raw
            || JsonNode.Parse(raw.ToString()) is not JsonObject match)
        {
            return;
        }

        if (!await redis.StringSetAsync($"{MatchKey(matchId)}:recorded", "1", TimeSpan.FromHours(1), When.NotExists))
        {
            return;
        }

        string playerId = Str(match, "playerId") ?? "";
        string slug = Str(match, "slug") ?? "";
        string chapterId = Str(match, "chapterId") ?? "";
        string nodeId = Str(match, "nodeId") ?? "";
        string lobbyId = Str(match, "lobbyId") ?? "";
        int difficulty = match["difficulty"] is JsonValue d && d.TryGetValue(out double n) ? (int)n : 0;
        // The player is on team 0 (RiftMatchService).
        bool won = winningTeamIndex == 0;

        var (dynamic, player) = await InstanceAsync(playerId, ct);
        List<string> earned = [], added = [];
        var unknown = new List<string>();
        if (won)
        {
            RecordWin(dynamic, slug, chapterId, nodeId, difficulty);
            var missions = RiftMissions.NodeMissions(slug, nodeId, difficulty);
            earned = RiftMissions.Earned(missions, new RiftMissionContext(true, Str(match, "character") ?? "", Str(match, "skin") ?? "", counters), unknown);
            added = RecordStars(player, slug, chapterId, nodeId, difficulty, earned);
            await Instances().UpdateOneAsync(new BsonDocument("account_id", playerId), new BsonDocument("$set", new BsonDocument
            {
                ["dynamic"] = BsonDocument.Parse(Js.Stringify(dynamic)),
                ["player"] = BsonDocument.Parse(Js.Stringify(player)),
                ["updatedAt"] = time.GetUtcNow().UtcDateTime,
            }), cancellationToken: ct);
            log.LogInformation("Rift match {Match} stars: node offers {Missions}; earned {Earned}; new {Added}{Unknown}", matchId,
                string.Join(", ", missions), string.Join(", ", earned), string.Join(", ", added),
                unknown.Count > 0 ? $"; could not judge {string.Join("; ", unknown.Distinct())}" : "");
        }

        var state = await states.StateAsync(playerId, ct);
        state["LastPlayed"] = new JsonObject { ["Chapter"] = chapterId, ["NodeId"] = nodeId, ["RiftSlug"] = slug };
        await states.SaveAsync(playerId, state, ct);

        log.LogInformation("Rift match {Match} of {Player}: {Result} node {Node} of {Rift} chapter {Chapter} difficulty {Difficulty}",
            matchId, playerId, won ? "won" : $"lost (winning team {winningTeamIndex?.ToString() ?? "none"})", nodeId, slug, chapterId, difficulty);

        await SendAsync(redis, playerId, Notification("OnLobbyRuntimeDataUpdated", lobbyId, "RuntimeData", RiftLobbyService.RuntimeData(dynamic, slug)));
        await SendAsync(redis, playerId, Notification("OnLobbyRiftStateUpdated", lobbyId, "RiftState", state));
        if (won)
        {
            // The game's handler (0x142a27740, bound to the router's OnPlayerInstanceUpdated event) reads RiftSlug, then
            // PlayerInstance (an object): that rift's entry.
            var message = Notification("OnPlayerInstanceUpdated", lobbyId, "PlayerInstance", player[slug] ?? new JsonObject());
            message["data"]!["RiftSlug"] = slug;
            await SendAsync(redis, playerId, message);
        }
    }

    /// <summary>Adds <paramref name="nodeId"/> to the chapter's completions at <paramref name="difficulty"/>, once.</summary>
    internal static void RecordWin(JsonObject dynamic, string slug, string chapterId, string nodeId, int difficulty)
    {
        var rift = Child(dynamic, slug);
        var chapter = Child(Child(rift, "RuntimeChapterData"), chapterId);
        chapter["bIsChapterComplete"] ??= false;
        chapter["HighestDifficultyCompleted"] ??= 0;
        chapter["CurrentDifficulty"] = difficulty;
        var byDifficulty = Child(chapter, "NodeCompletionsByDifficulty");
        string key = difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (byDifficulty[key] is not JsonArray nodes)
        {
            byDifficulty[key] = nodes = [];
        }

        if (!nodes.Any(n => n is JsonValue v && v.TryGetValue(out string? s) && s == nodeId))
        {
            nodes.Add(nodeId);
        }
    }

    /// <summary>
    /// Adds each earned mission to the node's CompletedMissions["<difficulty>"] (the format of the game's WB-era cache)
    /// and one to the chapter's CauldronsByDifficulty[difficulty].CurrentScore for each new one (in that cache the score
    /// is the number of missions completed at that difficulty, on every rift). Returns the new ones.
    /// </summary>
    internal static List<string> RecordStars(JsonObject player, string slug, string chapterId, string nodeId, int difficulty, IEnumerable<string> earned)
    {
        var rift = Child(player, slug);
        var node = Child(Child(rift, "RuntimeNodeData"), nodeId);
        node["ClaimedOneTimeRewardGuidsByDifficulty"] ??= new JsonObject();
        var completed = Child(node, "CompletedMissions");
        string key = difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (completed[key] is not JsonArray done)
        {
            completed[key] = done = [];
        }

        var added = new List<string>();
        foreach (string mission in earned)
        {
            if (!done.Any(n => n is JsonValue v && v.TryGetValue(out string? s) && s == mission))
            {
                done.Add(mission);
                added.Add(mission);
            }
        }

        if (added.Count > 0 && rift["RuntimeChapterData"]?[chapterId]?["CauldronsByDifficulty"] is JsonArray cauldrons
            && difficulty >= 0 && difficulty < cauldrons.Count && cauldrons[difficulty] is JsonObject cauldron)
        {
            cauldron["CurrentScore"] = (int)(RiftMissions.Number(cauldron["CurrentScore"]) ?? 0) + added.Count;
        }

        return added;
    }

    internal static void AddMissingRifts(JsonObject stored, JsonObject fresh)
    {
        foreach (var (slug, rift) in fresh)
        {
            if (!stored.ContainsKey(slug))
            {
                stored[slug] = rift?.DeepClone();
            }
        }
    }

    /// <summary>A new player's DynamicInstanceRuntimeData: the catalog's (the frozen copy and the generated rifts) with
    /// every chapter's progress cleared; CurrentDifficulty kept (-1 where none was chosen).</summary>
    internal static JsonObject NewDynamic()
    {
        var dynamic = RiftCatalog.Dynamic.DeepClone().AsObject();
        foreach (var (_, rift) in dynamic)
        {
            if (rift?["RuntimeChapterData"] is not JsonObject chapters)
            {
                continue;
            }

            foreach (var (_, chapter) in chapters)
            {
                if (chapter is JsonObject c)
                {
                    c["bIsChapterComplete"] = false;
                    c["NodeCompletionsByDifficulty"] = new JsonObject();
                    c["HighestDifficultyCompleted"] = 0;
                }
            }
        }

        return dynamic;
    }

    /// <summary>A new player's PlayerInstanceRuntimeData: the catalog's (the frozen copy, a new player's already, and the
    /// generated rifts).</summary>
    internal static JsonObject NewPlayer() => RiftCatalog.Player.DeepClone().AsObject();

    private static JsonObject Notification(string template, string lobbyId, string key, JsonNode value) => new()
    {
        ["data"] = new JsonObject { ["template_id"] = template, ["LobbyId"] = lobbyId, [key] = value.DeepClone() },
        ["payload"] = new JsonObject { ["custom_notification"] = "realtime" },
        ["header"] = "",
        ["cmd"] = "update",
    };

    private static Task SendAsync(IDatabase redis, string playerId, JsonObject message) =>
        redis.PublishAsync(RedisChannel.Literal(WsSendChannel), Js.Stringify(new JsonObject { ["playerIds"] = new JsonArray(playerId), ["message"] = message }));

    private IMongoCollection<BsonDocument> Instances() =>
        (services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)")).GetCollection<BsonDocument>(Collection);

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is not JsonObject child)
        {
            parent[key] = child = new JsonObject();
        }

        return child;
    }

    private static string? Str(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

// The TS server's match results (match:end_of_match_stats: {matchId, playerId, winningTeamIndex}), for rift progress.
internal sealed class RiftResultSubscriber(IServiceProvider services, IRiftProgressService progress, ILogger<RiftResultSubscriber> log) : IHostedService
{
    public const string Channel = "match:end_of_match_stats";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } redis)
        {
            return;
        }

        log.LogWarning("MIGRATION BRIDGE: rift progress is recorded from the TS server's {Channel} and sent through its websocket (ws:send); see dotnet/docs/MIGRATION-BRIDGES.md (4)", Channel);
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel), (channel, message) => _ = HandleAsync(message.ToString()));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleAsync(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject result || result["matchId"] is not JsonValue id || !id.TryGetValue(out string? matchId))
            {
                return;
            }

            int? winning = result["winningTeamIndex"] is JsonValue w && w.TryGetValue(out double n) ? (int)n : null;
            await progress.RecordResultAsync(matchId, winning, result["missionUpdates"] as JsonObject, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Nothing else would ever see it: this runs on the subscription's callback, not a request.
            log.LogError(e, "Rift result from {Message} not recorded: {Error}", message, e.Message);
        }
    }
}
