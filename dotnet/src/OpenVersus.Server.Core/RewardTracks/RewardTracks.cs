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
// tiers nobody earned. Two settings make tracks each player's own, starting from nothing: RewardTracks:CharacterMastery
// the character and account levels (mrt_mastery_*), RewardTracks:PerPlayer the others. A track neither makes the
// player's own is answered as the fixed answer has it, and nothing is added to it or claimed on it (both off: the fixed
// answer, byte for byte, as today on prod). For the player's own:
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
//     reached); ClaimedRewards changes only with a tier claim (ClaimAllAsync: claim_all_milestone_reward_track_tiers
//     {TrackSlug}, every reward of the completed tiers; the rewards themselves are not granted yet: their tables are
//     client data, docs/MISSIONS.md). Not done yet: bResetWhenCompleted (the
//     missions' bonus tracks start over once complete), the infinite last tier (bDoesLastTierRecurInfinitely).

/// <summary>Reward track settings.</summary>
public sealed class RewardTrackSettings
{
    [Description("Each player has their own reward tracks other than character and account levels (battle passes, the missions' bonus tracks, events), starting from nothing. Off: those tracks as the TS server's fixed answer has them, the same for everyone, and nothing is added to them.")]
    public bool PerPlayer { get; set; }

    [Description("Character and account levels (the mrt_mastery_* tracks) are each player's own, starting from zero and earned in matches. Off: as the TS server's fixed answer has them (every character at level 99), and no match XP is recorded.")]
    public bool CharacterMastery { get; set; }

    /// <summary>Whether <paramref name="slug"/> is a character or account level track (mrt_mastery_*).</summary>
    public static bool IsMastery(string slug) => slug.StartsWith("mrt_mastery_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="slug"/> is each player's own under these settings (else the fixed answer's).</summary>
    public bool Governs(string slug) => IsMastery(slug) ? CharacterMastery : PerPlayer;

    /// <summary>Whether any track is each player's own.</summary>
    public bool Any => PerPlayer || CharacterMastery;

    [Description("Matches add XP to the account and character mastery tracks (character and account levels; only with RewardTracks:CharacterMastery): 150 for a win, 50 for a loss (the game's XPSRC_Base; custom games only with Missions:CustomGamesProgress).")]
    public bool MatchXp { get; set; } = true;

    [Description("Rift matches add that XP too. The client's offline rift backend marks rifts as granting no progress (bModeGrantsProgress false), which says nothing about WB's online rifts.")]
    public bool RiftMatchXp { get; set; } = true;
}

public interface IRewardTrackService
{
    /// <summary>The get_milestone_reward_tracks answer for the player.</summary>
    Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct);

    /// <summary>Adds score to the player's tracks (slug to points); the tiers reached follow. Returns the changed tracks
    /// as the answer lists them (for RewardTrackStatesUpdated).</summary>
    Task<IReadOnlyList<JsonObject>> AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct);

    /// <summary>claim_all_milestone_reward_track_tiers: marks every reward of the track's completed tiers claimed.
    /// Returns the track as the answer lists it (null for a track the answer does not list) and the rewards newly
    /// claimed (the tiers' reward entries).</summary>
    Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAllAsync(string accountId, string trackSlug, CancellationToken ct);
}

internal sealed class RewardTrackService(IServiceProvider services, ILogger<RewardTrackService> log) : IRewardTrackService
{
    // Off by default (the settings' defaults): no track is the player's own.
    private RewardTrackSettings Settings => services.GetService<IOptionsMonitor<RewardTrackSettings>>()?.CurrentValue ?? new RewardTrackSettings();

    internal const string Collection = "rewardtracks";
    private const string Fixed = "ssc-get-milestone-reward-tracks";

    // The fixed answer's tracks (shared: read only).
    private static readonly Lazy<List<JsonObject>> s_tracks = new(() =>
        (JsonNode.Parse(StaticResponses.Json(Fixed))!["body"]!["RewardTrackStates"] as JsonArray ?? []).OfType<JsonObject>().ToList());

    public async Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct)
    {
        var settings = Settings;
        var stored = await StoredAsync(accountId, ct);
        var states = new JsonArray();
        foreach (var track in s_tracks.Value)
        {
            string slug = track["TrackSlug"]?.GetValue<string>() ?? "";
            states.Add(settings.Governs(slug) ? Entry(track, stored?[slug] as JsonObject ?? Initial(slug)) : track.DeepClone());
        }

        return new JsonObject { ["body"] = new JsonObject { ["RewardTrackStates"] = states }, ["metadata"] = null, ["return_code"] = 0 };
    }

    public async Task<IReadOnlyList<JsonObject>> AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct)
    {
        // Only the tracks that are the player's own (the others are the fixed answer's, and stay so).
        var settings = Settings;
        points = points.Where(p => settings.Governs(p.Key)).ToDictionary();
        if (points.Count == 0)
        {
            return [];
        }

        var tracks = await UpdateAsync(accountId, stored =>
        {
            foreach (var (slug, add) in points)
            {
                var state = stored[slug] as JsonObject ?? Initial(slug);
                stored[slug] = Scored(slug, state, Score(state) + add);
            }

            return true;
        }, ct);
        if (tracks is null)
        {
            log.LogWarning("Reward track score for {Account} lost ({Points})", accountId, string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")));
            return [];
        }

        return Changed(tracks, points.Keys);
    }

    public async Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAllAsync(string accountId, string trackSlug, CancellationToken ct)
    {
        if (!Settings.Governs(trackSlug))
        {
            return (null, []);
        }

        var claimed = new List<JsonObject>();
        var tracks = await UpdateAsync(accountId, stored =>
        {
            claimed.Clear();
            var state = stored[trackSlug] as JsonObject ?? Initial(trackSlug);
            var completed = (state["CompletedTiers"] as JsonArray ?? []).Select(t => t?.GetValue<string>()).ToHashSet();
            var had = state["ClaimedRewards"] as JsonArray ?? [];
            var have = had.Select(r => r?.GetValue<string>()).ToHashSet();
            foreach (var tier in (HissTables.Data("milestone-reward-tracks", trackSlug)?["Tiers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!completed.Contains(tier["TierGuid"]?.GetValue<string>()))
                {
                    continue;
                }

                foreach (var reward in (tier["Rewards"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (reward["RewardGuid"]?.GetValue<string>() is { } guid && have.Add(guid))
                    {
                        claimed.Add((JsonObject)reward.DeepClone());
                    }
                }
            }

            if (claimed.Count == 0)
            {
                return false;
            }

            var next = (JsonObject)state.DeepClone();
            next["ClaimedRewards"] = new JsonArray(had.Select(r => r?.DeepClone()).Concat(claimed.Select(r => r["RewardGuid"]!.DeepClone())).ToArray());
            stored[trackSlug] = next;
            return true;
        }, ct) ?? await StoredAsync(accountId, ct) ?? [];
        var track = s_tracks.Value.FirstOrDefault(t => t["TrackSlug"]?.GetValue<string>() == trackSlug);
        return (track is null ? null : Entry(track, tracks[trackSlug] as JsonObject ?? Initial(trackSlug)), claimed);
    }

    // Reads the player's tracks, lets change say whether it changed them, and writes them back (version-guarded,
    // retried when another request wrote between). The tracks as written (or as read, when nothing changed); null when
    // the write kept losing or there is no player.
    private async Task<JsonObject?> UpdateAsync(string accountId, Func<JsonObject, bool> change, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id))
        {
            return null;
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
            if (!change(tracks))
            {
                return tracks;
            }

            var next = new BsonDocument { { "_id", id }, { "tracks", BsonDocument.Parse(Compat.Js.Stringify(tracks)) }, { "version", version + 1 } };
            try
            {
                if (doc is null)
                {
                    await collection.InsertOneAsync(next, cancellationToken: ct);
                    return tracks;
                }

                if ((await collection.ReplaceOneAsync(new BsonDocument { { "_id", id }, { "version", version } }, next, cancellationToken: ct)).MatchedCount == 1)
                {
                    return tracks;
                }
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Created by another request meanwhile: change that.
            }
        }

        return null;
    }

    // The changed tracks as the answer lists them, in its order (tracks it does not list are left out).
    private static List<JsonObject> Changed(JsonObject tracks, IEnumerable<string> slugs) =>
        s_tracks.Value
            .Where(t => t["TrackSlug"] is JsonValue v && v.TryGetValue(out string? slug) && slugs.Contains(slug) && tracks[slug] is JsonObject)
            .Select(t => Entry(t, tracks[t["TrackSlug"]!.GetValue<string>()]!.AsObject()))
            .ToList();

    private static long Score(JsonObject state) => (long)(RiftsNumber(state["CurrentScore"]) ?? 0);

    // The state at a new score: the tiers whose threshold it reached are completed (in the track's order); claims kept.
    internal static JsonObject Scored(string slug, JsonObject state, long score)
    {
        var reached = (Hiss.HissTables.Data("milestone-reward-tracks", slug)?["Tiers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(t => (RiftsNumber(t["ScoreThreshold"]) ?? double.MaxValue) <= score)
            .ToList();
        var next = (JsonObject)state.DeepClone();
        next["CurrentScore"] = score <= int.MaxValue ? JsonValue.Create((int)score) : JsonValue.Create(score);
        next["CurrentTier"] = reached.Count;
        next["CompletedTiers"] = new JsonArray(reached.Select(t => (JsonNode?)t["TierGuid"]?.DeepClone()).ToArray());
        return next;
    }

    private static double? RiftsNumber(JsonNode? node) => Rifts.RiftMissions.Number(node);

    // A track as the answer lists it: the fixed answer's fields around the player's state.
    private static JsonObject Entry(JsonObject track, JsonObject state) => new()
    {
        ["TrackSlug"] = track["TrackSlug"]?.DeepClone(),
        ["RewardTrackClass"] = track["RewardTrackClass"]?.DeepClone(),
        ["CurrentScore"] = state["CurrentScore"]?.DeepClone() ?? 0,
        ["CurrentTier"] = state["CurrentTier"]?.DeepClone() ?? 0,
        ["CompletedTiers"] = state["CompletedTiers"]?.DeepClone() ?? new JsonArray(),
        ["ClaimedRewards"] = state["ClaimedRewards"]?.DeepClone() ?? new JsonArray(),
        ["bHasPremium"] = track["bHasPremium"]?.DeepClone(),
        ["Guid"] = track["Guid"]?.DeepClone(),
        ["InfiniteTierThreshold"] = track["InfiniteTierThreshold"]?.DeepClone(),
        ["HighestClaimedInifiniteTier"] = state["HighestClaimedInifiniteTier"]?.DeepClone() ?? -1,
    };

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
        builder.Services.AddSingleton<IRewardGrants, RewardGrants>();
        return builder;
    }
}
