using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Rifts;

namespace OpenVersus.Server.Core.RewardTracks;

// Paying rewards: a reward as the hiss writes one (a mission list entry's RewardData, a reward track tier's Rewards:
// {RewardGrantMethod, RewardGuid, Constraints, and RewardHsda or InventoryHsda + DirectInventoryItemCount}).
//
//   RewardTableLookup   RewardHsda through the reward tables (reward-data.json, generated from the game's own assets
//                       by tools/rewards/gen_reward_data.mjs): currency, xp, item, gem, lootbox, or another reward
//   DirectInventoryItem InventoryHsda x DirectInventoryItemCount: a currency when the tables know it as one, else an item
//
// Every grant is recorded, whatever the inventory already shows (every player owns every item today; content added
// later will not be): match_toasts on playercounters (as the TS server's adjustMatchToasts, an atomic $inc; the TS
// websocket reads it), everything else on playeritems {_id: the player's ObjectId, items: {slug: count}} (C# only; the
// inventory answer lists what it holds that unlock-all does not). XP goes to the tracks with the reward's tag that a
// live mission container names in its RewardTracksToAdvance (XP:Event:MRT:Battlepass: the Season 5 battle pass, not
// the earlier ones with the same tag); a tag no live track has (Fighter Road, a dead feature; character level-up XP) is
// recorded nowhere and logged. Lootboxes are counted, never opened (not built).

/// <summary>A reward resolved to what it pays.</summary>
public sealed record Grant(string Kind, string Target, long Count);

/// <summary>What paying some rewards did: the rewards as the client is told (OnRewardsGranted) and the tracks XP moved.</summary>
public sealed record GrantResult(JsonArray RewardsGranted, IReadOnlyList<JsonObject> ChangedTracks);

public interface IRewardGrants
{
    /// <summary>Pays <paramref name="rewards"/> to the player; XP goes to the tracks the live containers name.</summary>
    Task<GrantResult> GrantAsync(string accountId, IReadOnlyList<JsonObject> rewards, IReadOnlyList<string> liveContainers, CancellationToken ct);

    /// <summary>The player's recorded items and currencies (slug to count), match_toasts aside.</summary>
    Task<IReadOnlyDictionary<string, long>> ItemsAsync(string accountId, CancellationToken ct);
}

public static class RewardData
{
    private static readonly Lazy<JsonObject> s_data = new(() =>
    {
        using var stream = typeof(RewardData).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.RewardTracks.reward-data.json")
            ?? throw new InvalidOperationException("reward-data.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    private static readonly Lazy<HashSet<string>> s_currencies = new(() =>
        (s_data.Value["rewards"] as JsonObject ?? []).Select(kv => kv.Value).OfType<JsonObject>()
            .Where(r => Text(r["type"]) == "currency").Select(r => Text(r["target"])).OfType<string>().ToHashSet());

    /// <summary>The inventory items the reward tables pay as currencies (perk_currency, match_toasts, gold, ...).</summary>
    public static IReadOnlySet<string> Currencies => s_currencies.Value;

    /// <summary>A character's mastery track (its CharacterData MrtSlug), or null.</summary>
    public static string? CharacterTrack(string character) => Text(s_data.Value["characterTracks"]?[character]);

    /// <summary>A match XP source by slug (lower case): {type, base, winModifier, lossModifier, drawModifier}.</summary>
    public static JsonObject? XpSource(string slug) => s_data.Value["xpSources"]?[slug.ToLowerInvariant()] as JsonObject;

    /// <summary>What <paramref name="reward"/> pays; <paramref name="unknown"/> collects what could not be resolved.</summary>
    public static List<Grant> Resolve(JsonObject reward, ICollection<string> unknown)
    {
        var grants = new List<Grant>();
        switch (Text(reward["RewardGrantMethod"]))
        {
            case "RewardTableLookup":
                Lookup(Text(reward["RewardHsda"]), 1, grants, unknown, depth: 0);
                break;
            case "DirectInventoryItem" when Text(reward["InventoryHsda"]) is { Length: > 0 } item:
                long count = (long)(RiftMissions.Number(reward["DirectInventoryItemCount"]) ?? 1);
                grants.Add(new Grant(Currencies.Contains(item) ? "currency" : "item", item, count));
                break;
            default:
                unknown.Add($"reward {reward.ToJsonString()}");
                break;
        }

        return grants;
    }

    private static void Lookup(string? slug, long times, List<Grant> grants, ICollection<string> unknown, int depth)
    {
        if (slug is null || s_data.Value["rewards"]?[slug] is not JsonObject entry || depth > 3)
        {
            unknown.Add($"reward table {slug ?? "(none)"}");
            return;
        }

        string? type = Text(entry["type"]), target = Text(entry["target"]);
        long count = (long)(RiftMissions.Number(entry["count"]) ?? 1) * times;
        if (type == "reward")
        {
            Lookup(target, count, grants, unknown, depth + 1);
        }
        else if (type is "currency" or "xp" or "item" or "gem" or "lootbox" && target is { Length: > 0 })
        {
            grants.Add(new Grant(type, target, count));
        }
        else
        {
            unknown.Add($"reward table {slug} ({type})");
        }
    }

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

internal sealed class RewardGrants(IServiceProvider services, IRewardTrackService tracks, TimeProvider time, ILogger<RewardGrants> log) : IRewardGrants
{
    public const string Collection = "playeritems";

    public async Task<GrantResult> GrantAsync(string accountId, IReadOnlyList<JsonObject> rewards, IReadOnlyList<string> liveContainers, CancellationToken ct)
    {
        var granted = new JsonArray();
        if (rewards.Count == 0 || !ObjectId.TryParse(accountId, out var id))
        {
            return new GrantResult(granted, []);
        }

        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var unknown = new SortedSet<string>();
        var items = new Dictionary<string, long>();
        long toasts = 0;
        var points = new Dictionary<string, int>();
        var liveTracks = LiveTracks(liveContainers);
        foreach (var reward in rewards)
        {
            foreach (var grant in RewardData.Resolve(reward, unknown))
            {
                switch (grant.Kind)
                {
                    case "currency" when grant.Target == "match_toasts":
                        toasts += grant.Count;
                        break;
                    case "xp":
                        var to = liveTracks.Where(t => HissTables.Data("milestone-reward-tracks", t)?["XpRewardGrantTag"] is JsonValue v
                            && v.TryGetValue(out string? tag) && tag == grant.Target).ToList();
                        if (to.Count == 0)
                        {
                            unknown.Add($"xp {grant.Target} x{grant.Count} (no live track takes it)");
                        }

                        foreach (string track in to)
                        {
                            points[track] = (int)Math.Min(int.MaxValue, points.GetValueOrDefault(track) + grant.Count);
                        }

                        break;
                    default:
                        if (grant.Target.Contains('.') || grant.Target.StartsWith('$'))
                        {
                            unknown.Add($"{grant.Kind} {grant.Target} (not a storable slug)");
                            break;
                        }

                        items[grant.Target] = items.GetValueOrDefault(grant.Target) + grant.Count;
                        break;
                }
            }

            granted.Add(reward.DeepClone());
        }

        if (toasts > 0)
        {
            // As the TS server's adjustMatchToasts: the document first (with its defaults), then an atomic $inc.
            await DailyToastBonus.GetCountersAsync(mongo, accountId, time.GetUtcNow(), ct);
            await mongo.GetCollection<BsonDocument>(DailyToastBonus.Collection).UpdateOneAsync(
                new BsonDocument("accountId", accountId), new BsonDocument("$inc", new BsonDocument("match_toasts", toasts)), cancellationToken: ct);
        }

        if (items.Count > 0)
        {
            var inc = new BsonDocument();
            foreach (var (slug, count) in items)
            {
                inc[$"items.{slug}"] = count;
            }

            await mongo.GetCollection<BsonDocument>(Collection).UpdateOneAsync(
                new BsonDocument("_id", id), new BsonDocument("$inc", inc), new UpdateOptions { IsUpsert = true }, ct);
        }

        var changed = points.Count > 0 ? await tracks.AddScoreAsync(accountId, points, ct) : [];
        log.LogInformation("Rewards paid to {Account}: {Items}{Toasts}{Xp}{Unknown}", accountId,
            string.Join(", ", items.Select(i => $"{i.Key} x{i.Value}")),
            toasts > 0 ? $"; match_toasts +{toasts}" : "",
            points.Count > 0 ? "; xp " + string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")) : "",
            unknown.Count > 0 ? "; not paid: " + string.Join("; ", unknown) : "");
        return new GrantResult(granted, changed);
    }

    public async Task<IReadOnlyDictionary<string, long>> ItemsAsync(string accountId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id) || services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return new Dictionary<string, long>();
        }

        var doc = await mongo.GetCollection<BsonDocument>(Collection).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return doc?.GetValue("items", BsonNull.Value) is BsonDocument items
            ? items.Where(e => e.Value.IsNumeric).ToDictionary(e => e.Name, e => e.Value.ToInt64())
            : new Dictionary<string, long>();
    }

    // The tracks the live containers name in their RewardTracksToAdvance.
    internal static HashSet<string> LiveTracks(IReadOnlyList<string> liveContainers) =>
        liveContainers
            .SelectMany(c => HissTables.Data("mission-containers", c)?["MvsMissionControllerContainerData"]?["RewardTracksToAdvance"] as JsonArray ?? [])
            .Select(t => RewardData.Text(t?["RewardTrack"]))
            .OfType<string>()
            .ToHashSet();
}
