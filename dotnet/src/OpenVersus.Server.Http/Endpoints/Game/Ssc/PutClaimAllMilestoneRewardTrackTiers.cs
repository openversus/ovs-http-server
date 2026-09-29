using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/claim_all_milestone_reward_track_tiers.
/// Seen in: binary ssc name; captured 1x.
/// Ssc: binary (probable).
/// </summary>
public sealed class PutClaimAllMilestoneRewardTrackTiers : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/claim_all_milestone_reward_track_tiers");
    }
}
