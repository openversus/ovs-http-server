using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.RewardTracks;

// A second season of Fighter Passes without a reset (Jacob, 2026-10-08). Each fighter's track (mrt_mastery_*, an
// MvsCharacterMasteryRewardTrackHsda) ends in a tier the game draws as the wide infinity card: its DisplayType is
// EMvsTierDisplayType::Infinite (2), not its place, says so, and get_milestone_reward_tracks' InfiniteTierThreshold
// (15) is that tier's index. With FighterPass:ExtraTiers set, that tier becomes a Milestone card (1) and the extra
// tiers follow it, FighterPass:TierXp apart, the last one Infinite: 16 extra tiers make 17-32, the end at 65,000.
// Players keep their score; one at the old end goes on towards the new tiers. The extra tiers repeat the track's own
// first rewards (Toasts, and battle pass XP every fifth); the last is FighterPass:FinalReward ({fighter} is the track's
// fighter, as in skin_ovs_chromium_{fighter}), or the reward before the old end while there is none.
//
// The tables the server reads (HissTables, so the score cap, the tiers reached and their payment) and the hiss the game
// downloads (HissService) are extended alike, from the current settings. Off (0 extra tiers) changes nothing.
// Turning it on, off or changing it is a hiss change like any other: bump the config CRC with it (POST /syncAsset's
// UpdateCrc), or games keep the hiss they have and still show 16 tiers (seen in the first test, 2026-10-08).

/// <summary>The Fighter Pass extension settings.</summary>
public sealed class FighterPassSettings
{
    [Description("Tiers added after each fighter's last tier (End Game: 16, its tiers 17-32), the old last tier becoming a Milestone card and the new last the infinity card. 0: off, the tracks as the game's data has them.")]
    [Range(0, 64)]
    public int ExtraTiers { get; set; }

    [Description("XP between two extra tiers (2,500: 16 extra tiers end at 65,000).")]
    [Range(100, 1000000)]
    public int TierXp { get; set; } = 2500;

    [Description("The last extra tier's reward: an inventory item, {fighter} standing for the track's fighter (skin_ovs_gold_{fighter}, say). Empty: the reward before the old last tier (battle pass XP), until the item exists.")]
    public string FinalReward { get; set; } = "";
}

internal static class FighterPass
{
    internal const string MasteryPrefix = "mrt_mastery_";
    private const string MasteryClass = "MvsCharacterMasteryRewardTrackHsda";
    private const int Normal = 0;
    private const int Milestone = 1;
    private const int Infinite = 2;

    private static volatile FighterPassSettings s_current = new();

    /// <summary>The settings in force for this process (kept current by <see cref="FighterPassSync"/>).</summary>
    internal static FighterPassSettings Current => s_current;

    internal static void Use(FighterPassSettings settings) => s_current = settings;

    /// <summary>What the extension makes of the tables: equal keys, equal tables.</summary>
    internal static string Key(FighterPassSettings settings) =>
        settings.ExtraTiers <= 0 ? "" : $"{settings.ExtraTiers}:{settings.TierXp}:{settings.FinalReward}";

    /// <summary>
    /// The milestone-reward-tracks table ({slug: {slug, data}}) with every fighter track extended; the table itself
    /// when the extension is off. Never changes <paramref name="table"/>.
    /// </summary>
    internal static JsonObject Extend(JsonObject table, FighterPassSettings settings)
    {
        if (settings.ExtraTiers <= 0)
        {
            return table;
        }

        var extended = (JsonObject)table.DeepClone();
        foreach (var (slug, entry) in extended)
        {
            if (entry?["data"] is JsonObject data && Extends(slug, data))
            {
                ExtendTrack(slug, data, settings);
            }
        }

        return extended;
    }

    /// <summary>A fighter's track that ends in an infinity tier: the tracks the extension extends.</summary>
    internal static bool Extends(string slug, JsonObject data) =>
        slug.StartsWith(MasteryPrefix, StringComparison.Ordinal) && slug != MasteryPrefix + "account"
        && data["RewardTrackClass"] is JsonValue c && c.TryGetValue(out string? cls) && cls == MasteryClass
        && data["Tiers"] is JsonArray { Count: > 1 } tiers && DisplayType(tiers[^1]) == Infinite;

    /// <summary>The index of the infinity tier of <paramref name="slug"/> in the extended tables, or null when it is not extended.</summary>
    internal static int? InfiniteTierIndex(string slug)
    {
        var settings = Current;
        return settings.ExtraTiers > 0 && Hiss.HissTables.GameData("milestone-reward-tracks", slug) is { } data && Extends(slug, data)
            ? ((JsonArray)data["Tiers"]!).Count - 1 + settings.ExtraTiers
            : null;
    }

    private static void ExtendTrack(string slug, JsonObject data, FighterPassSettings settings)
    {
        var tiers = (JsonArray)data["Tiers"]!;
        var last = (JsonObject)tiers[^1]!;
        double end = Number(last["ScoreThreshold"]);
        last["DisplayType"] = Milestone;
        string fighter = slug[MasteryPrefix.Length..];
        int original = tiers.Count;
        for (int k = 1; k <= settings.ExtraTiers; k++)
        {
            int tierNumber = original + k;
            bool final = k == settings.ExtraTiers;
            JsonArray rewards = final ? FinalRewards(slug, tierNumber, tiers, original, fighter, settings) : Repeated(slug, tierNumber, (JsonObject)tiers[(k - 1) % (original - 1)]!);
            tiers.Add(new JsonObject
            {
                ["DisplayType"] = final ? Infinite : Normal,
                ["Rewards"] = rewards,
                ["ScoreThreshold"] = end + (double)k * settings.TierXp,
                ["TierGuid"] = Guid(slug, tierNumber, "tier"),
            });
        }
    }

    // A tier's rewards again, under new guids (claims and payments go by guid).
    private static JsonArray Repeated(string slug, int tierNumber, JsonObject source)
    {
        var rewards = new JsonArray();
        int i = 0;
        foreach (var reward in (source["Rewards"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var copy = (JsonObject)reward.DeepClone();
            copy["RewardGuid"] = Guid(slug, tierNumber, $"reward{i++}");
            rewards.Add(copy);
        }

        return rewards;
    }

    private static JsonArray FinalRewards(string slug, int tierNumber, JsonArray tiers, int original, string fighter, FighterPassSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FinalReward))
        {
            return Repeated(slug, tierNumber, (JsonObject)tiers[original - 2]!);
        }

        return
        [
            new JsonObject
            {
                ["Constraints"] = new JsonArray(),
                ["DirectInventoryItemCount"] = 1,
                ["InventoryHsda"] = settings.FinalReward.Trim().Replace("{fighter}", fighter, StringComparison.Ordinal),
                ["RewardGrantMethod"] = "DirectInventoryItem",
                ["RewardGuid"] = Guid(slug, tierNumber, "reward0"),
            },
        ];
    }

    // 32 hex digits, as the game's own tier and reward guids: the same for the same track, tier and part, so a tier
    // claimed under one start of the server is the same tier under the next.
    private static string Guid(string slug, int tierNumber, string part) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"ovs-fighter-pass:{slug}:{tierNumber}:{part}")));

    private static int DisplayType(JsonNode? tier) => tier?["DisplayType"] is JsonValue v && v.TryGetValue(out int type) ? type : Normal;

    private static double Number(JsonNode? node) => Rifts.RiftMissions.Number(node) ?? 0;
}

/// <summary>Keeps <see cref="FighterPass.Current"/> on the FighterPass settings, as they are changed at run time too.</summary>
internal sealed class FighterPassSync(IOptionsMonitor<FighterPassSettings> settings) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken ct)
    {
        FighterPass.Use(settings.CurrentValue);
        _subscription = settings.OnChange(FighterPass.Use);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() => _subscription?.Dispose();
}
