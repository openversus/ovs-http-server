using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Rifts;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Missions;

// Each player's missions (Missions:Enabled), rolled from the hiss (mission-containers -> mission-controlers ->
// mission-list -> missions) for the live containers (Missions:Containers). docs/MISSIONS.md has the evidence; in short,
// read off a WB account's object and the client:
//
//   - a controller holds Missions (groups of {slug: {MissionObjectives [{Slug, Progress}], MissionGuid}}, the granted
//     missions not yet claimed) and UsedMissions (every slug ever granted, in order);
//   - a roll grants each of the container's controllers Count missions from its list, by the container's GrantBehavior:
//     DescendingOrderByWeight the heaviest (bForce first, then by Weight, list order on ties; used ones not skipped),
//     RandomByWeight a weighted draw (bForce first, then from the slugs not yet used, all of them once used up),
//     Unlockable the whole list once, as one group; the others one group per mission;
//   - a refresh adds groups and keeps the unfinished ones (bCleanRefresh is false on every container used here; its
//     meaning is not known, so it is not acted on): Daily and Weekly containers roll again when read after a reset
//     (MissionClock) passed since their last roll, once however many passed (bEventRefreshCatchup false); the others
//     (RefreshRate None) roll once;
//   - a finished mission is answered with bIsClaimable: true: the game offers the claim only then (bench, 2026-10-01),
//     though WB's own object had finished missions without it.
//
// Mongo, C# only: missionobjects {_id: the player's ObjectId, object_id (the object's own id, as WB's had one),
// server_data {MissionControllerContainers, ClaimLocks}, rolled {container: last roll (date)}, created_at, updated_at,
// version}. The answer carries the live containers only (a container dropped from the setting keeps its stored state).
// Two reads racing (the login batch) both roll; the first write wins and the other answers what was stored (version).
//
// Progress from match results: MissionProgress.cs.
//
// claim_mission_rewards {ContainerSlug, MissionsToClaim: [{MissionControllerSlug, MissionGuid, MissionSlug}]}: each named
// mission that is finished (every objective at its Count, or any one when bAllObjectivesMustBeCompleted is false) leaves
// its group (an emptied group goes), and the container's RewardTracksToAdvance gain: MissionScore the mission's
// ScoreContribution (the XP the game shows on it), Incremental 1 per mission (ContainerOverrideValue, 1 on every
// container), through IRewardTrackService. A mission not found or not finished is left as it is. The answer is the
// player's server_data after it ({MissionControllerContainers, ClaimLocks}): the shape the TS server's fixed answer has,
// and the client hands it to the routine that takes the whole mission object (docs/MISSIONS.md; a hypothesis until the
// bench shows it). Not yet: the list entries' RewardData (item and reward-table rewards: the tables are client data).
//
// Not yet: FTUE (the client treats miscon_ftue apart; its daily-login controller
// is not moved by matches), attempt_daily_refresh's PlayerMissionObject (sent empty, as before).

public interface IMissionService
{
    /// <summary>The get_or_create_mission_object answer for the player, rolling what is due first.</summary>
    Task<JsonObject> GetOrCreateAsync(string accountId, CancellationToken ct);

    /// <summary>claim_mission_rewards: claims the finished missions named, answering the player's missions after it.</summary>
    Task<JsonObject> ClaimAsync(string accountId, JsonObject body, CancellationToken ct);

    /// <summary>Moves the player's missions by a match result (MissionProgress), once per player and match.</summary>
    Task RecordMatchAsync(string matchId, string playerId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct);
}

internal sealed class MissionService(IServiceProvider services, IOptionsMonitor<MissionSettings> settings, TimeProvider time, MissionRandom random, ILogger<MissionService> log) : IMissionService
{
    private const string Collection = "missionobjects";

    public async Task<JsonObject> GetOrCreateAsync(string accountId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id))
        {
            // No player to keep missions for: the fixed answer's shape with no containers.
            return MissionObject.Answer(accountId, enabled: false);
        }

        var collection = (services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)"))
            .GetCollection<BsonDocument>(Collection);
        var current = settings.CurrentValue;
        var live = LiveContainers(current.Containers);
        for (int attempt = 0; ; attempt++)
        {
            var stored = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            var state = stored is null ? MissionState.New(time.GetUtcNow()) : MissionState.From(stored);
            // After a lost race, what the other request stored is the answer.
            bool rolled = attempt == 0 && Roll(state, live, current, time.GetUtcNow());
            if (!rolled)
            {
                return Answer(state, accountId, live);
            }

            state.UpdatedAt = time.GetUtcNow();
            if (stored is null)
            {
                try
                {
                    await collection.InsertOneAsync(state.ToBson(id), cancellationToken: ct);
                    return Answer(state, accountId, live);
                }
                catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    log.LogInformation("Missions for {Account} were created by another request: answering those", accountId);
                    continue;
                }
            }

            var result = await collection.ReplaceOneAsync(
                new BsonDocument { { "_id", id }, { "version", state.Version } }, state.Next().ToBson(id), cancellationToken: ct);
            if (result.MatchedCount == 1)
            {
                return Answer(state, accountId, live);
            }

            log.LogInformation("Missions for {Account} were rolled by another request: answering those", accountId);
        }
    }

    public async Task<JsonObject> ClaimAsync(string accountId, JsonObject body, CancellationToken ct)
    {
        var current = settings.CurrentValue;
        var live = LiveContainers(current.Containers);
        string? containerSlug = Text(body["ContainerSlug"]);
        if (!ObjectId.TryParse(accountId, out var id))
        {
            return ClaimAnswer(MissionState.New(time.GetUtcNow()), live);
        }

        var collection = (services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)"))
            .GetCollection<BsonDocument>(Collection);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct) is not { } stored)
            {
                return ClaimAnswer(MissionState.New(time.GetUtcNow()), live);
            }

            var state = MissionState.From(stored);
            var claimed = containerSlug is not null && live.Contains(containerSlug)
                ? Claim(state, containerSlug, body["MissionsToClaim"] as JsonArray ?? [])
                : [];
            if (claimed.Count == 0)
            {
                log.LogInformation("Mission claim by {Account} in {Container}: nothing finished to claim ({Body})", accountId, containerSlug, Js.Stringify(body));
                return ClaimAnswer(state, live);
            }

            state.UpdatedAt = time.GetUtcNow();
            var result = await collection.ReplaceOneAsync(
                new BsonDocument { { "_id", id }, { "version", state.Version } }, state.Next().ToBson(id), cancellationToken: ct);
            if (result.MatchedCount != 1)
            {
                continue;
            }

            var points = TrackPoints(containerSlug!, claimed);
            log.LogInformation("Missions claimed by {Account} in {Container}: {Missions}; reward tracks {Points}", accountId, containerSlug,
                string.Join(", ", claimed), string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")));
            await services.GetRequiredService<RewardTracks.IRewardTrackService>().AddScoreAsync(accountId, points, ct);
            return ClaimAnswer(state, live);
        }

        log.LogWarning("Mission claim by {Account} lost: the missions kept changing under it", accountId);
        return ClaimAnswer(MissionState.From((await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct))!), live);
    }

    /// <summary>Removes the finished missions named from the container; the slugs claimed.</summary>
    internal static List<string> Claim(MissionState state, string containerSlug, JsonArray requested)
    {
        var claimed = new List<string>();
        if (state.ServerData["MissionControllerContainers"]?[containerSlug]?["MissionControllers"] is not JsonObject controllers)
        {
            return claimed;
        }

        foreach (var request in requested.OfType<JsonObject>())
        {
            string? controllerSlug = Text(request["MissionControllerSlug"]), guid = Text(request["MissionGuid"]), slug = Text(request["MissionSlug"]);
            if (controllerSlug is null || guid is null || slug is null || controllers[controllerSlug]?["Missions"] is not JsonArray groups)
            {
                continue;
            }

            var group = groups.OfType<JsonObject>().FirstOrDefault(g => g[slug] is JsonObject m && Text(m["MissionGuid"]) == guid);
            if (group is null || !Finished(slug, group[slug]!.AsObject()))
            {
                continue;
            }

            group.Remove(slug);
            if (group.Count == 0)
            {
                groups.Remove(group);
            }

            claimed.Add(slug);
        }

        return claimed;
    }

    // Every objective at its Count, or any one when the mission does not need them all.
    internal static bool Finished(string slug, JsonObject mission)
    {
        var definition = HissTables.Data("missions", slug)?["MvsMissionData"] as JsonObject;
        var counts = (definition?["MissionObjectives"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var done = (mission["MissionObjectives"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (RiftMissions.Number(o["Progress"]) ?? 0) >= (RiftMissions.Number(counts.FirstOrDefault(c => Text(c["ObjectivePtr"]) == Text(o["Slug"]))?["Count"]) ?? 1))
            .ToList();
        bool all = Bool(definition?["bAllObjectivesMustBeCompleted"]) ?? false;
        return done.Count > 0 && (all ? done.All(d => d) : done.Any(d => d));
    }

    // What the claimed missions add to the container's reward tracks.
    internal static Dictionary<string, int> TrackPoints(string containerSlug, IReadOnlyList<string> claimed)
    {
        var points = new Dictionary<string, int>();
        var tracks = HissTables.Data("mission-containers", containerSlug)?["MvsMissionControllerContainerData"]?["RewardTracksToAdvance"] as JsonArray ?? [];
        foreach (var track in tracks.OfType<JsonObject>())
        {
            string? slug = Text(track["RewardTrack"]);
            if (slug is null)
            {
                continue;
            }

            int add = Text(track["ScoreGrantBehavior"]) switch
            {
                "MissionScore" => claimed.Sum(m => (int)(RiftMissions.Number(HissTables.Data("missions", m)?["MvsMissionData"]?["ScoreContribution"]) ?? 0)),
                "Incremental" => claimed.Count * (int)(RiftMissions.Number(track["ContainerOverrideValue"]) ?? 1),
                _ => 0,
            };
            if (add > 0)
            {
                points[slug] = points.GetValueOrDefault(slug) + add;
            }
        }

        return points;
    }

    private static JsonObject ClaimAnswer(MissionState state, IReadOnlyList<string> live) =>
        new() { ["body"] = Answer(state, "", live)["body"]!["server_data"]!.DeepClone(), ["metadata"] = null, ["return_code"] = 0 };

    public async Task RecordMatchAsync(string matchId, string playerId, int? winningTeamIndex, JsonObject? counters, CancellationToken ct)
    {
        if (!ObjectId.TryParse(playerId, out var id) || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return;
        }

        if (!await redis.StringSetAsync($"mission_match:{matchId}:{playerId}", "1", TimeSpan.FromHours(1), When.NotExists))
        {
            return;
        }

        if (await JsonAtAsync(redis, matchId) is not JsonObject notification)
        {
            log.LogWarning("Mission progress for {Player}: match {Match} is not known (no notification at its key)", playerId, matchId);
            return;
        }

        var current = settings.CurrentValue;
        var config = notification["gameplayConfigOverride"] as JsonObject;
        bool custom = Bool(config?["bIsCustomGame"]) ?? Bool(notification["isCustomGame"]) ?? false;
        if (custom && !current.CustomGamesProgress)
        {
            return;
        }

        var player = (notification["players"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => Text(p["playerId"]) == playerId);
        bool won = player is not null && winningTeamIndex is { } w && RiftMissions.Number(player["teamIndex"]) == w;
        string character = "", skin = "";
        if (await JsonAtAsync(redis, $"rift_match:{matchId}") is JsonObject rift && Text(rift["playerId"]) == playerId)
        {
            character = Text(rift["character"]) ?? "";
            skin = Text(rift["skin"]) ?? "";
        }
        else
        {
            var loadout = await redis.HashGetAsync($"player:{playerId}", ["character", "skin"]);
            character = loadout[0].ToString();
            skin = loadout[1].ToString();
        }

        var match = new MissionMatch(won, character, skin, Text(config?["ModeString"]) ?? Text(notification["mode"]) ?? "",
            Text(config?["Map"]) ?? Text(notification["map"]) ?? "", Bool(config?["bIsPvP"]) ?? true, counters);
        var live = LiveContainers(current.Containers);
        var collection = (services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)"))
            .GetCollection<BsonDocument>(Collection);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct) is not { } stored)
            {
                return;
            }

            var state = MissionState.From(stored);
            var unknown = new SortedSet<string>();
            if (!MissionRules.Apply(state.ServerData, live, match, unknown))
            {
                Report(unknown, playerId, matchId);
                return;
            }

            state.UpdatedAt = time.GetUtcNow();
            var result = await collection.ReplaceOneAsync(
                new BsonDocument { { "_id", id }, { "version", state.Version } }, state.Next().ToBson(id), cancellationToken: ct);
            if (result.MatchedCount == 1)
            {
                Report(unknown, playerId, matchId);
                log.LogInformation("Missions for {Player} moved by match {Match} ({Character}, {Mode}, {Map}, PvP {PvP})", playerId, matchId, character, match.Mode, match.Map, match.IsPvP);
                await redis.PublishAsync(RedisChannel.Literal(RiftProgressService.WsSendChannel), Js.Stringify(new JsonObject
                {
                    ["playerIds"] = new JsonArray(playerId),
                    ["message"] = UpdatesComplete(Answer(state, playerId, live)["body"]!.DeepClone().AsObject(), playerId),
                }));
                return;
            }
        }

        log.LogWarning("Mission progress for {Player} from match {Match} lost: the missions kept changing under it", playerId, matchId);
    }

    // MissionUpdatesComplete as WB's websocket sent it (a profile notification; the object with ISO text dates).
    private static JsonObject UpdatesComplete(JsonObject body, string playerId)
    {
        foreach (string date in new[] { "updated_at", "created_at" })
        {
            if (RiftMissions.Number(body[date]?["_hydra_unix_date"]) is { } seconds)
            {
                body[date] = DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return new JsonObject
        {
            ["data"] = new JsonObject { ["template_id"] = "MissionUpdatesComplete", ["data"] = body },
            ["payload"] = new JsonObject
            {
                ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
                ["template"] = "realtime",
                ["account_id"] = playerId,
                ["profile_id"] = playerId,
            },
            ["header"] = "",
            ["cmd"] = "profile-notification",
        };
    }

    private void Report(ICollection<string> unknown, string playerId, string matchId)
    {
        if (unknown.Count > 0)
        {
            log.LogWarning("Mission progress for {Player} from match {Match}: not judged: {Unknown}", playerId, matchId, string.Join("; ", unknown));
        }
    }

    // The JSON at a key; null when the key is missing or empty.
    private static async Task<JsonNode?> JsonAtAsync(IDatabase redis, string key)
    {
        var value = await redis.StringGetAsync(key);
        return value.IsNullOrEmpty ? null : Js.Parse(value.ToString());
    }

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The live container slugs, in the setting's order: those the hiss has and enables.</summary>
    internal static List<string> LiveContainers(string setting)
    {
        var table = HissTables.Table("mission-containers");
        var live = new List<string>();
        foreach (string entry in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = entry.EndsWith('*')
                ? table.Select(kv => kv.Key).Where(k => k.StartsWith(entry[..^1], StringComparison.Ordinal))
                : table.ContainsKey(entry) ? [entry] : [];
            foreach (string slug in matches)
            {
                if (!live.Contains(slug) && HissTables.Data("mission-containers", slug) is { } data
                    && data["bIsEnabled"]?.GetValue<bool>() != false && data["MvsMissionControllerContainerData"] is JsonObject)
                {
                    live.Add(slug);
                }
            }
        }

        return live;
    }

    /// <summary>Rolls every live container that is due; true when anything was granted.</summary>
    internal bool Roll(MissionState state, IReadOnlyList<string> live, MissionSettings current, DateTimeOffset now)
    {
        bool any = false;
        foreach (string slug in live)
        {
            var container = HissTables.Data("mission-containers", slug)!["MvsMissionControllerContainerData"]!.AsObject();
            string rate = container["RefreshRate"]?.GetValue<string>() ?? "None";
            var last = state.Rolled.TryGetValue(slug, out var at) ? at : (DateTimeOffset?)null;
            bool due = last is null
                || (rate == "Daily" && last < MissionClock.LastDaily(current, now))
                || (rate == "Weekly" && last < MissionClock.LastWeekly(current, now));
            if (!due)
            {
                continue;
            }

            var containerState = state.Container(slug);
            string behavior = container["GrantBehavior"]?.GetValue<string>() ?? "";
            foreach (var controllerSlug in (container["MissionControllers"] as JsonArray ?? []).Select(n => n?.GetValue<string>()))
            {
                if (HissTables.Data("mission-controlers", controllerSlug) is not { } controllerData
                    || controllerData["bIsEnabled"]?.GetValue<bool>() == false
                    || controllerData["MvsMissionController"] is not JsonObject controller)
                {
                    continue;
                }

                var entries = (HissTables.Data("mission-list", controller["MissionList"]?.GetValue<string>())?["MvsMissionListData"]?["MissionList"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Where(e => HissTables.Data("missions", e["Mission"]?.GetValue<string>()) is not null)
                    .ToList();
                var controllerState = MissionState.Controller(containerState, controllerSlug!);
                var used = controllerState["UsedMissions"]!.AsArray();
                var picks = Pick(entries, behavior, controller["Count"]?.GetValue<int>() ?? 0, used.Select(u => u?.GetValue<string>()).ToHashSet());
                if (picks.Count == 0)
                {
                    continue;
                }

                var missions = controllerState["Missions"]!.AsArray();
                if (behavior == "Unlockable")
                {
                    var group = new JsonObject();
                    foreach (string mission in picks)
                    {
                        group[mission] = NewMission(mission);
                    }

                    missions.Add(group);
                }
                else
                {
                    foreach (string mission in picks)
                    {
                        missions.Add(new JsonObject { [mission] = NewMission(mission) });
                    }
                }

                foreach (string mission in picks)
                {
                    used.Add(mission);
                }

                any = true;
            }

            state.Rolled[slug] = now;
        }

        return any;
    }

    /// <summary>The missions a roll grants from a controller's list.</summary>
    internal List<string> Pick(List<JsonObject> entries, string behavior, int count, HashSet<string?> used)
    {
        string Slug(JsonObject e) => e["Mission"]!.GetValue<string>();
        int Weight(JsonObject e) => e["Weight"]?.GetValue<int>() ?? 0;
        if (behavior == "Unlockable")
        {
            return entries.Select(Slug).Distinct().ToList();
        }

        var picks = entries.Where(e => e["bForce"]?.GetValue<bool>() == true).Select(Slug).Distinct().Take(count).ToList();
        var rest = entries.Where(e => !picks.Contains(Slug(e))).ToList();
        if (behavior == "DescendingOrderByWeight")
        {
            // OrderByDescending is stable: list order on ties.
            foreach (var e in rest.OrderByDescending(Weight))
            {
                if (picks.Count >= count)
                {
                    break;
                }

                if (!picks.Contains(Slug(e)))
                {
                    picks.Add(Slug(e));
                }
            }

            return picks;
        }

        // RandomByWeight (and anything unknown): weighted, without repeats in one roll, unused slugs first.
        var pool = rest.Where(e => !used.Contains(Slug(e))).ToList();
        if (pool.Count < count - picks.Count)
        {
            pool = rest;
        }

        while (picks.Count < count && pool.Count > 0)
        {
            int total = pool.Sum(e => Math.Max(Weight(e), 0));
            int index;
            if (total == 0)
            {
                index = random.Next(pool.Count);
            }
            else
            {
                int roll = random.Next(total);
                index = 0;
                while (roll >= Math.Max(Weight(pool[index]), 0))
                {
                    roll -= Math.Max(Weight(pool[index]), 0);
                    index++;
                }
            }

            string slug = Slug(pool[index]);
            picks.Add(slug);
            pool.RemoveAll(e => Slug(e) == slug);
        }

        return picks;
    }

    // {MissionObjectives: [{Slug, Progress 0}], MissionGuid}: the WB object's key order.
    private JsonObject NewMission(string slug)
    {
        var objectives = new JsonArray();
        foreach (var objective in (HissTables.Data("missions", slug)?["MvsMissionData"]?["MissionObjectives"] as JsonArray ?? []).OfType<JsonObject>())
        {
            objectives.Add(new JsonObject { ["Slug"] = objective["ObjectivePtr"]?.GetValue<string>(), ["Progress"] = 0 });
        }

        return new JsonObject { ["MissionObjectives"] = objectives, ["MissionGuid"] = random.NewGuid().ToString() };
    }

    // The fixed answer's envelope, in its key order, around the player's object.
    // A finished mission is marked bIsClaimable: true (last), which the game needs to offer the claim (bench,
    // 2026-10-01: a mission at 400/400 without it was not claimable). Worked out at every answer, not stored.
    private static JsonNode Claimable(JsonNode container)
    {
        foreach (var (_, controller) in container["MissionControllers"] as JsonObject ?? [])
        {
            foreach (var (slug, mission) in (controller?["Missions"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(g => g).ToList())
            {
                if (mission is JsonObject m && Finished(slug, m))
                {
                    m["bIsClaimable"] = true;
                }
            }
        }

        return container;
    }

    private static JsonObject Answer(MissionState state, string accountId, IReadOnlyList<string> live)
    {
        var containers = new JsonObject();
        var stored = state.ServerData["MissionControllerContainers"] as JsonObject ?? [];
        foreach (string slug in live)
        {
            if (stored[slug] is { } container)
            {
                containers[slug] = Claimable(container.DeepClone());
            }
        }

        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["updated_at"] = new JsonObject { ["_hydra_unix_date"] = state.UpdatedAt.ToUnixTimeSeconds() },
                ["owner_id"] = accountId,
                ["unique_key"] = "missions",
                ["object_type_slug"] = "player-missions",
                ["server_data"] = new JsonObject
                {
                    ["MissionControllerContainers"] = containers,
                    ["ClaimLocks"] = state.ServerData["ClaimLocks"]?.DeepClone() ?? new JsonObject(),
                },
                ["created_at"] = new JsonObject { ["_hydra_unix_date"] = state.CreatedAt.ToUnixTimeSeconds() },
                ["aggregates"] = new JsonObject(),
                ["calculations"] = new JsonObject(),
                ["id"] = state.ObjectId.ToString(),
                ["owner"] = new JsonObject(),
                ["expire_time"] = null,
                ["owner_model"] = "account",
            },
            ["metadata"] = null,
            ["return_code"] = 0,
        };
    }
}

/// <summary>A player's stored missions, as read (or new).</summary>
internal sealed class MissionState
{
    public ObjectId ObjectId { get; private init; }
    public JsonObject ServerData { get; private init; } = [];
    public Dictionary<string, DateTimeOffset> Rolled { get; } = [];
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; private set; }

    public static MissionState New(DateTimeOffset now) => new()
    {
        ObjectId = ObjectId.GenerateNewId(),
        ServerData = new JsonObject { ["MissionControllerContainers"] = new JsonObject(), ["ClaimLocks"] = new JsonObject() },
        CreatedAt = now,
        UpdatedAt = now,
    };

    public static MissionState From(BsonDocument doc)
    {
        var state = new MissionState
        {
            ObjectId = doc.GetValue("object_id", ObjectId.GenerateNewId()).AsObjectId,
            ServerData = JsonNode.Parse(doc.GetValue("server_data", new BsonDocument()).AsBsonDocument.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }))!.AsObject(),
            CreatedAt = doc.GetValue("created_at", BsonNull.Value) is BsonDateTime c ? new DateTimeOffset(c.ToUniversalTime()) : DateTimeOffset.UnixEpoch,
            UpdatedAt = doc.GetValue("updated_at", BsonNull.Value) is BsonDateTime u ? new DateTimeOffset(u.ToUniversalTime()) : DateTimeOffset.UnixEpoch,
            Version = doc.GetValue("version", 0).ToInt64(),
        };
        if (doc.GetValue("rolled", BsonNull.Value) is BsonDocument rolled)
        {
            foreach (var e in rolled)
            {
                if (e.Value is BsonDateTime at)
                {
                    state.Rolled[e.Name] = new DateTimeOffset(at.ToUniversalTime());
                }
            }
        }

        state.ServerData["MissionControllerContainers"] ??= new JsonObject();
        state.ServerData["ClaimLocks"] ??= new JsonObject();
        return state;
    }

    /// <summary>This state with the version a replace writes (the filter keeps the old one).</summary>
    public MissionState Next()
    {
        Version++;
        return this;
    }

    public BsonDocument ToBson(ObjectId accountId)
    {
        var rolled = new BsonDocument();
        foreach (var (slug, at) in Rolled)
        {
            rolled[slug] = new BsonDateTime(at.UtcDateTime);
        }

        return new BsonDocument
        {
            { "_id", accountId },
            { "object_id", ObjectId },
            { "server_data", BsonDocument.Parse(Js.Stringify(ServerData)) },
            { "rolled", rolled },
            { "created_at", new BsonDateTime(CreatedAt.UtcDateTime) },
            { "updated_at", new BsonDateTime(UpdatedAt.UtcDateTime) },
            { "version", Version },
        };
    }

    /// <summary>The container's state ({MissionControllers: {}}), made when missing.</summary>
    public JsonObject Container(string slug)
    {
        var containers = ServerData["MissionControllerContainers"]!.AsObject();
        if (containers[slug] is not JsonObject container)
        {
            container = new JsonObject { ["MissionControllers"] = new JsonObject() };
            containers[slug] = container;
        }

        return container;
    }

    /// <summary>The controller's state ({Missions: [], UsedMissions: []}) in a container's, made when missing.</summary>
    public static JsonObject Controller(JsonObject container, string slug)
    {
        var controllers = container["MissionControllers"] as JsonObject ?? [];
        container["MissionControllers"] = controllers;
        if (controllers[slug] is not JsonObject controller)
        {
            controller = new JsonObject { ["Missions"] = new JsonArray(), ["UsedMissions"] = new JsonArray() };
            controllers[slug] = controller;
        }

        return controller;
    }
}

/// <summary>The rolls' randomness: a seam so tests can fix it.</summary>
internal class MissionRandom
{
    public virtual int Next(int maxExclusive) => Random.Shared.Next(maxExclusive);

    public virtual Guid NewGuid() => Guid.NewGuid();
}
