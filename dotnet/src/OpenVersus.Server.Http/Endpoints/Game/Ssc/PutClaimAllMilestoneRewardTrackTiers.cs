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
/// PUT /ssc/invoke/claim_all_milestone_reward_track_tiers {TrackSlug} (captured: {"TrackSlug":
/// "mrt_battlepass_season_five"}): for a track that is the player's own (RewardTracks:PerPlayer, CharacterMastery), every reward of the track's completed tiers is marked
/// claimed (<see cref="IRewardTrackService.ClaimAllAsync"/>), the answer is {RewardTrackStates: [the track],
/// RewardsGranted: [the tier rewards paid]} (the fields of the client's OnMilestoneRewardTrackTiersClaimed; WB's answer
/// was never captured), the rewards are paid (<see cref="IRewardGrants"/>) and the game is sent RewardTrackStatesUpdated
/// (UpdateContext RewardTrackClaim). Another track: as the TS server answers, its catch-all.
/// Seen in: binary ssc name; captured 1x (answered by the TS catch-all).
/// </summary>
public sealed class PutClaimAllMilestoneRewardTrackTiers : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/claim_all_milestone_reward_track_tiers");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject ?? [];
        string accountId = HttpContext.Session()?.AccountId ?? "";
        Logger.LogInformation("claim_all_milestone_reward_track_tiers by {Account}: {Body}", accountId, Js.Stringify(body));
        if (body["TrackSlug"] is not JsonValue v || !v.TryGetValue(out string? trackSlug)
            || !Resolve<IOptionsMonitor<RewardTrackSettings>>().CurrentValue.Governs(trackSlug))
        {
            await SendJsonAsync(await TsCatchAll.AnswerAsync(TryResolve<IMongoDatabase>(), ct), ct);
            return;
        }

        var states = new JsonArray();
        var granted = new JsonArray();
        try
        {
            var (track, claimed) = await Resolve<IRewardTrackService>().ClaimAllAsync(accountId, trackSlug, ct);
            if (track is not null)
            {
                var live = MissionContainers.Live(Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue.Containers);
                var paid = await Resolve<IRewardGrants>().GrantAsync(accountId, claimed, live, ct);
                granted = paid.RewardsGranted;
                // The claimed track, as XP from its own rewards may have moved it, and any other track XP moved.
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
