using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.RewardTracks;

// GET /ssc/invoke/get_milestone_reward_tracks: the player's reward tracks (battle passes, character and account
// mastery, Fighter Road, the missions' bonus tracks, events). The TS server answers one fixed state for everyone
// (Static/ssc-get-milestone-reward-tracks.json, copied from a WB account, every track at tier 99), so the game offers
// tiers nobody earned. With RewardTracks:PerPlayer each player has their own, starting from nothing:
//
//   - the tracks, their order and their fixed fields (RewardTrackClass, Guid, bHasPremium, InfiniteTierThreshold) are
//     the fixed answer's (Guid is per track, not per player: a WB-era cache of another account has the same ones);
//   - a track never earned has CurrentScore 0, and as WB counted (its cache: CurrentTier is the number of tiers whose
//     ScoreThreshold the score reached, CompletedTiers their TierGuids) the tiers at threshold 0 are reached: the first
//     tier of the battle passes and Fighter Roads. Their rewards are listed as claimed, so nothing can be claimed that
//     was not earned (WB left them to claim);
//   - stored states (Mongo rewardtracks {_id: the player's ObjectId, tracks: {slug: {CurrentScore, CurrentTier,
//     CompletedTiers, ClaimedRewards, HighestClaimedInifiniteTier}}, version}, C# only) win. Claimed missions add to
//     them (AddScoreAsync): the score grows, and CurrentTier and CompletedTiers follow it (the tiers whose threshold it
//     reached); ClaimedRewards changes only with a tier claim (not built yet). Not done yet: bResetWhenCompleted (the
//     missions' bonus tracks start over once complete), the infinite last tier (bDoesLastTierRecurInfinitely).

/// <summary>Reward track settings.</summary>
public sealed class RewardTrackSettings
{
    [Description("Each player has their own reward tracks (battle passes, character levels, the missions' bonus tracks), starting from nothing. Off: the TS server's fixed answer, the same for everyone (every track at tier 99, with tiers to claim nobody earned).")]
    public bool PerPlayer { get; set; } = true;
}

public interface IRewardTrackService
{
    /// <summary>The get_milestone_reward_tracks answer for the player.</summary>
    Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct);

    /// <summary>Adds score to the player's tracks (slug to points); the tiers reached follow.</summary>
    Task AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct);
}

internal sealed class RewardTrackService(IServiceProvider services, ILogger<RewardTrackService> log) : IRewardTrackService
{
    internal const string Collection = "rewardtracks";
    private const string Fixed = "ssc-get-milestone-reward-tracks";

    // The fixed answer's tracks (shared: read only).
    private static readonly Lazy<List<JsonObject>> s_tracks = new(() =>
        (JsonNode.Parse(StaticResponses.Json(Fixed))!["body"]!["RewardTrackStates"] as JsonArray ?? []).OfType<JsonObject>().ToList());

    public async Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct)
    {
        var stored = await StoredAsync(accountId, ct);
        var states = new JsonArray();
        foreach (var track in s_tracks.Value)
        {
            string slug = track["TrackSlug"]?.GetValue<string>() ?? "";
            var state = stored?[slug] as JsonObject ?? Initial(slug);
            states.Add(new JsonObject
            {
                ["TrackSlug"] = slug,
                ["RewardTrackClass"] = track["RewardTrackClass"]?.DeepClone(),
                ["CurrentScore"] = state["CurrentScore"]?.DeepClone() ?? 0,
                ["CurrentTier"] = state["CurrentTier"]?.DeepClone() ?? 0,
                ["CompletedTiers"] = state["CompletedTiers"]?.DeepClone() ?? new JsonArray(),
                ["ClaimedRewards"] = state["ClaimedRewards"]?.DeepClone() ?? new JsonArray(),
                ["bHasPremium"] = track["bHasPremium"]?.DeepClone(),
                ["Guid"] = track["Guid"]?.DeepClone(),
                ["InfiniteTierThreshold"] = track["InfiniteTierThreshold"]?.DeepClone(),
                ["HighestClaimedInifiniteTier"] = state["HighestClaimedInifiniteTier"]?.DeepClone() ?? -1,
            });
        }

        return new JsonObject { ["body"] = new JsonObject { ["RewardTrackStates"] = states }, ["metadata"] = null, ["return_code"] = 0 };
    }

    public async Task AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct)
    {
        if (points.Count == 0 || !ObjectId.TryParse(accountId, out var id))
        {
            return;
        }

        var collection = (services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)"))
            .GetCollection<BsonDocument>(Collection);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var doc = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            long version = doc?.GetValue("version", 0).ToInt64() ?? 0;
            var tracks = doc?.GetValue("tracks", BsonNull.Value) is BsonDocument stored
                ? JsonNode.Parse(stored.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }))!.AsObject()
                : new JsonObject();
            foreach (var (slug, add) in points)
            {
                var state = tracks[slug] as JsonObject ?? Initial(slug);
                tracks[slug] = Scored(slug, state, Score(state) + add);
            }

            var next = new BsonDocument { { "_id", id }, { "tracks", BsonDocument.Parse(Compat.Js.Stringify(tracks)) }, { "version", version + 1 } };
            try
            {
                if (doc is null)
                {
                    await collection.InsertOneAsync(next, cancellationToken: ct);
                    return;
                }

                if ((await collection.ReplaceOneAsync(new BsonDocument { { "_id", id }, { "version", version } }, next, cancellationToken: ct)).MatchedCount == 1)
                {
                    return;
                }
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Created by another request meanwhile: add to that.
            }
        }

        log.LogWarning("Reward track score for {Account} lost: the tracks kept changing under it ({Points})", accountId, string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")));
    }

    private static long Score(JsonObject state) => (long)(RiftsNumber(state["CurrentScore"]) ?? 0);

    // The state at a new score: the tiers whose threshold it reached are completed (in the track's order); claims kept.
    internal static JsonObject Scored(string slug, JsonObject state, long score)
    {
        var reached = (Hiss.HissTables.Data("milestone-reward-tracks", slug)?["Tiers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(t => (RiftsNumber(t["ScoreThreshold"]) ?? double.MaxValue) <= score)
            .ToList();
        var next = (JsonObject)state.DeepClone();
        next["CurrentScore"] = score;
        next["CurrentTier"] = reached.Count;
        next["CompletedTiers"] = new JsonArray(reached.Select(t => (JsonNode?)t["TierGuid"]?.DeepClone()).ToArray());
        return next;
    }

    private static double? RiftsNumber(JsonNode? node) => Rifts.RiftMissions.Number(node);

    /// <summary>A track never earned: score 0, the tiers at threshold 0 reached and their rewards claimed.</summary>
    internal static JsonObject Initial(string slug)
    {
        var free = (HissTables.Data("milestone-reward-tracks", slug)?["Tiers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(t => t["ScoreThreshold"] is JsonValue v && v.TryGetValue(out double threshold) && threshold <= 0)
            .ToList();
        return new JsonObject
        {
            ["CurrentScore"] = 0,
            ["CurrentTier"] = free.Count,
            ["CompletedTiers"] = new JsonArray(free.Select(t => (JsonNode?)t["TierGuid"]?.DeepClone()).ToArray()),
            ["ClaimedRewards"] = new JsonArray(free
                .SelectMany(t => (t["Rewards"] as JsonArray ?? []).OfType<JsonObject>())
                .Select(r => (JsonNode?)r["RewardGuid"]?.DeepClone()).ToArray()),
            ["HighestClaimedInifiniteTier"] = -1,
        };
    }

    // The player's stored tracks, or null (none, no Mongo, or no player id: everything as never earned).
    private async Task<JsonObject?> StoredAsync(string accountId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id) || services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return null;
        }

        try
        {
            var doc = await mongo.GetCollection<BsonDocument>(Collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            return doc?.GetValue("tracks", BsonNull.Value) is BsonDocument tracks
                ? JsonNode.Parse(tracks.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }))!.AsObject()
                : null;
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError("Reward tracks for {Account} could not be read: answering them as never earned ({Error})", accountId, e.Message);
            return null;
        }
    }
}

public static class RewardTrackHosting
{
    public static WebApplicationBuilder AddRewardTracks(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RewardTrackSettings>("RewardTracks");
        builder.Services.AddSingleton<IRewardTrackService, RewardTrackService>();
        return builder;
    }
}
