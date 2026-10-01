using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Http.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/claim_all_milestone_reward_track_tiers {TrackSlug} (captured: {"TrackSlug":
/// "mrt_battlepass_season_five"}): with RewardTracks:PerPlayer, every reward of the track's completed tiers is marked
/// claimed (<see cref="IRewardTrackService.ClaimAllAsync"/>), the answer is {RewardTrackStates: [the track],
/// RewardsGranted: []} (the fields of the client's OnMilestoneRewardTrackTiersClaimed; WB's answer was never captured)
/// and the game is sent RewardTrackStatesUpdated (UpdateContext RewardTrackClaim). The rewards themselves are not
/// granted yet (their tables are client data: docs/MISSIONS.md). Off: as the TS server answers, its catch-all.
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
        if (!Resolve<IOptionsMonitor<RewardTrackSettings>>().CurrentValue.PerPlayer
            || body["TrackSlug"] is not JsonValue v || !v.TryGetValue(out string? trackSlug))
        {
            await SendJsonAsync(await TsCatchAll.AnswerAsync(TryResolve<IMongoDatabase>(), ct), ct);
            return;
        }

        var states = new JsonArray();
        try
        {
            var (track, claimed) = await Resolve<IRewardTrackService>().ClaimAllAsync(accountId, trackSlug, ct);
            if (track is not null)
            {
                states.Add(track.DeepClone());
                Logger.LogInformation("Reward track {Track} tiers claimed by {Account}: {Rewards} (not granted: reward tables not known yet)",
                    trackSlug, accountId, string.Join(", ", claimed.Select(r => Js.Stringify(r["InventoryHsda"] ?? r["RewardHsda"]))));
                if (claimed.Count > 0 && Resolve<IServiceProvider>().GetService(typeof(IConnectionMultiplexer)) is IConnectionMultiplexer redis)
                {
                    await ProfileNotifications.SendAsync(redis.GetDatabase(), accountId, ProfileNotifications.RewardTrackStatesUpdated([track], 1));
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
        {
            Logger.LogError("Reward track claim could not be made: answering nothing claimed ({Error})", e.Message);
        }

        await SendJsonAsync(new JsonObject
        {
            ["body"] = new JsonObject { ["RewardTrackStates"] = states, ["RewardsGranted"] = new JsonArray() },
            ["metadata"] = null,
            ["return_code"] = 0,
        }, ct);
    }
}
