using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;

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
//   - no bIsClaimable: WB's own object had finished missions without it, and the client works it out.
//
// Mongo, C# only: missionobjects {_id: the player's ObjectId, object_id (the object's own id, as WB's had one),
// server_data {MissionControllerContainers, ClaimLocks}, rolled {container: last roll (date)}, created_at, updated_at,
// version}. The answer carries the live containers only (a container dropped from the setting keeps its stored state).
// Two reads racing (the login batch) both roll; the first write wins and the other answers what was stored (version).
//
// Not yet: progress (match results), claims, FTUE (the client treats miscon_ftue apart; its daily-login controller
// is not moved by matches), attempt_daily_refresh's PlayerMissionObject (sent empty, as before).

public interface IMissionService
{
    /// <summary>The get_or_create_mission_object answer for the player, rolling what is due first.</summary>
    Task<JsonObject> GetOrCreateAsync(string accountId, CancellationToken ct);
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
    private static JsonObject Answer(MissionState state, string accountId, IReadOnlyList<string> live)
    {
        var containers = new JsonObject();
        var stored = state.ServerData["MissionControllerContainers"] as JsonObject ?? [];
        foreach (string slug in live)
        {
            if (stored[slug] is { } container)
            {
                containers[slug] = container.DeepClone();
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
