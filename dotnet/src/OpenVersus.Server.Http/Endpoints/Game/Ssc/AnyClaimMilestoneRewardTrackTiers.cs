using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/claim_milestone_reward_track_tiers {TrackSlug, and the tiers: TierGuids, Tiers or TierGuid}:
/// as claim_all_milestone_reward_track_tiers (<see cref="PutClaimAllMilestoneRewardTrackTiers"/>), limited to the listed
/// completed tiers (<see cref="IRewardTrackService.ClaimAsync"/>); no tier listed claims them all. The body was never
/// captured: the TS server (End Game) took the same three fields and logged the body, and so does this.
/// Seen in: binary ssc name.
/// Ssc: binary (probable).
/// </summary>
public sealed class AnyClaimMilestoneRewardTrackTiers : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/claim_milestone_reward_track_tiers");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject ?? [];
        string accountId = HttpContext.Session()?.AccountId ?? "";
        Logger.LogInformation("claim_milestone_reward_track_tiers by {Account}: {Body}", accountId, Js.Stringify(body));
        if (body["TrackSlug"] is not JsonValue v || !v.TryGetValue(out string? trackSlug)
            || !Resolve<IOptionsMonitor<RewardTrackSettings>>().CurrentValue.Governs(trackSlug))
        {
            await SendJsonAsync(await TsCatchAll.AnswerAsync(HttpContext.RequestServices, ct), ct);
            return;
        }

        var tiers = new[] { body["TierGuids"], body["Tiers"] }.OfType<JsonArray>().SelectMany(a => a)
            .Append(body["TierGuid"])
            .Select(t => t is JsonValue g && g.TryGetValue(out string? guid) ? guid : null)
            .OfType<string>()
            .ToHashSet();
        var states = new JsonArray();
        var granted = new JsonArray();
        try
        {
            var (track, claimed) = await Resolve<IRewardTrackService>().ClaimAsync(accountId, trackSlug, tiers.Count > 0 ? tiers : null, ct);
            if (track is not null)
            {
                var live = MissionContainers.Live(Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue.Containers);
                var paid = await Resolve<IRewardGrants>().GrantAsync(accountId, claimed, live, ct);
                granted = paid.RewardsGranted;
                var changed = paid.ChangedTracks.ToDictionary(t => t["TrackSlug"]!.GetValue<string>());
                var latest = changed.GetValueOrDefault(trackSlug) ?? track;
                states.Add(latest.DeepClone());
                changed[trackSlug] = latest;
                if (claimed.Count > 0 && TryResolve<IConnectionMultiplexer>() is { } redis)
                {
                    await ProfileNotifications.SendAsync(redis.GetDatabase(), accountId, ProfileNotifications.RewardTrackStatesUpdated(changed.Values, 1));
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
        {
            Logger.LogError("Reward track claim could not be made: answering nothing claimed ({Error})", e.Message);
        }

        await SendJsonAsync(new JsonObject
        {
            ["body"] = new JsonObject { ["RewardTrackStates"] = states, ["RewardsGranted"] = granted },
            ["metadata"] = null,
            ["return_code"] = 0,
        }, ct);
    }
}
